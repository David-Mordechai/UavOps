using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MoavAgent.Simulation;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Entry point the chat hub calls for one operator turn. Every turn goes straight to the single,
/// long-lived, memory-carrying <c>BrainAgent</c> (see
/// <see cref="AgentFactory.GetOrCreatePersistentBrainAgentAsync"/>).
///
/// This used to be gated behind a two-stage retry: a brand-new, memory-less <c>BrainAgent</c> tried
/// first, falling back to the memory-carrying one only when the stateless attempt produced zero tool
/// calls. That existed because of a real production incident: once the memory-carrying session had
/// one real "speed/altitude updated" turn in its history, the model then in use (granite4.1:3b, and
/// later a smaller Nemotron variant) would answer a near-identical follow-up command in well under a
/// second with zero tool calls — pattern-completing its own prior success instead of actually
/// delegating the new command, reporting a UAV action as done that never happened.
///
/// That gate is gone. After moving to a model chosen specifically for tool-calling reliability
/// (`nvidia/Qwen3.6-35B-A3B-NVFP4`, served locally via vLLM — see `appsettings.json`), the fabrication
/// this class used to structurally prevent was re-tested directly, harder than the original
/// incident's own validation: with the gate temporarily forced off (so the persistent, history-
/// carrying agent handled every turn), 10 near-identical "set UAV-1 speed to N" commands were sent
/// in a row through the same live session, each with more prior "success" turns already in context
/// than the last. All 10 produced a real, correctly-parameterized tool call — no fabrication. Real
/// conversation history is what actually makes cross-turn reference resolution possible ("what UAVs
/// do we have?" → "UAV-1" → "fly it to alpha"), which a memory-less agent can never do regardless of
/// model quality — so once the model was shown not to need the workaround, keeping it would have
/// only cost real conversational ability for no remaining safety benefit.
///
/// What this does NOT change: `BrainAgent` is still the only agent with memory. A specialist reached
/// through delegation (`FlightControlAgent`, `MoavAgent`, etc.) is still built fresh per turn with no
/// history of its own — `BrainAgent.yaml`'s own instructions require it to resolve any reference to
/// something established earlier in the conversation *itself*, before delegating, and to hand every
/// delegate a fully explicit, self-contained instruction exactly as if the operator had stated it
/// directly this turn. And the deterministic guards that protect against something no amount of model
/// quality fixes — `ConfirmationGate`/`OperatorPromptGate` requiring real operator sign-off before a
/// consequential action, `TailNumberDisambiguationTool`'s grounding of every tail number against the
/// real fleet — are untouched; those were never compensating for unreliability, they're an
/// intentional safety boundary.
///
/// **Verified retry for BrainAgent's own CreatePlan call.** Distinct from the removed two-stage
/// gate described above — that was a heuristic run *before* knowing whether the model would
/// fabricate; this instead checks ground truth *after* each attempt. Live testing (2026-09-05,
/// after switching to Qwen/Qwen3.8-27B-FP8) reproduced BrainAgent answering a real, actionable
/// fleet-wide command ("fly all of them...") with a fully fabricated, confident success report and
/// zero tool calls at all - no CreatePlan invocation, so nothing anywhere in the tree actually ran.
/// A dozens-of-requests direct backend repro of the exact same request shape (real CreatePlan
/// schema, real multi-turn history) could not reproduce it even once, so this looks like the same
/// low-probability "ignores tool_choice=required" backend flake already proven for leaf agents -
/// just biting at the one point where a single miss means *zero* delegation instead of a partial
/// one. <see cref="RequireToolOnFirstTurnChatClient"/> forcing the tool choice is necessary but,
/// per that evidence, not sufficient - this is the defense-in-depth for when it still slips through.
///
/// After each attempt, <see cref="ToolInvocationLogger.GetAgentInvocationCount"/> for
/// <see cref="AgentFactory.RootAgentName"/> is the ground truth: BrainAgent's only tool is
/// CreatePlan, so any real count above zero means it was actually invoked (an empty-steps plan
/// still counts - it's a real, valid call, not a fabrication). A zero count means the completion
/// that just returned never called anything, exactly the incident above - roll the persistent
/// session's history back to before this attempt (via <see cref="InMemoryChatHistoryProvider"/>'s
/// own snapshot/restore, since <c>RunAsync</c> already appended the fabricated turn to it) and
/// retry, up to <see cref="MaxAttempts"/> total. If every attempt fabricates, roll back one final
/// time and record an honest failure turn instead of ever showing the operator invented success
/// text - this keeps BrainAgent's own memory consistent with what it told the operator, so a later
/// turn referencing "that command" doesn't inherit a false success from history.
/// </summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory, ToolInvocationLogger toolLogger, ILogger<MainAgentOrchestrator> logger)
{
    private const int MaxAttempts = 3;

    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var (brainAgent, session, historyProvider) = await agentFactory.GetOrCreatePersistentBrainAgentAsync(cancellationToken);
        var tools = await agentFactory.BuildRootToolsForTurn(correlationId, text, cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = tools, AllowMultipleToolCalls = true });

        var historyBeforeThisTurn = new List<ChatMessage>(historyProvider.GetMessages(session));

        var responseText = string.Empty;
        var verified = false;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await brainAgent.RunAsync(text, session, runOptions, cancellationToken: cancellationToken);

            if (toolLogger.GetAgentInvocationCount(correlationId, AgentFactory.RootAgentName) > 0)
            {
                responseText = response.Text;
                verified = true;
                break;
            }

            logger.LogWarning(
                "correlationId={CorrelationId} BrainAgent attempt {Attempt}/{MaxAttempts} produced zero CreatePlan invocations for an actionable message - rolling back and retrying. Fabricated text: {FabricatedText}",
                correlationId, attempt, MaxAttempts, response.Text);

            // Undo whatever RunAsync just appended (the fabricated turn) before retrying, so the
            // retry sees the exact same history as this attempt did - and so the fabricated claim
            // never lingers in BrainAgent's memory even if a later attempt succeeds.
            historyProvider.SetMessages(session, new List<ChatMessage>(historyBeforeThisTurn));
            responseText = response.Text; // only kept in case this was the last attempt
        }

        if (!verified)
        {
            // Deliberately generic rather than "that command didn't go through" - a zero-invocation
            // completion isn't only produced by a refused/fabricated command. Live testing
            // (2026-09-05) found the backend deterministically never calls CreatePlan for a plain
            // conversational message either (a greeting failed 15/15 direct reproductions, byte-
            // identical at temperature 0) - retrying that is guaranteed pointless, but there's no
            // reliable, non-heuristic way from here to tell "refused an action" apart from "correctly
            // had nothing to delegate," so the wording has to read sensibly for either.
            responseText = "I wasn't able to get a clear, verified response for that after several attempts. If you were expecting an action to happen, please try again.";

            // Deliberately NOT committed to BrainAgent's own history (history is already rolled
            // back to historyBeforeThisTurn by the loop above - this turn leaves no trace there at
            // all, not even the operator's own message). Directly verified live (2026-09-05): once
            // written to history, this exact sentence caused every subsequent turn in the same
            // session - regardless of what the operator asked - to come back as that identical
            // sentence verbatim, with zero tool calls, deterministically (0/10 direct reproductions
            // once poisoned, vs. 10/10 success with clean history for the same follow-up command).
            // A degenerate repetition trap, not a fluke - an unusual, templated assistant utterance
            // sitting in context is a known trigger for this kind of model getting "stuck" on it.
            // Silently forgetting a failed turn is a strictly better failure mode than permanently
            // wedging the rest of the conversation.
            logger.LogError(
                "correlationId={CorrelationId} BrainAgent never produced a verified CreatePlan invocation after {MaxAttempts} attempts - returning an honest failure instead of fabricated text, without recording this turn in its own history.",
                correlationId, MaxAttempts);
        }

        sw.Stop();
        toolLogger.ClearInvocationTracking(correlationId);

        // Final safety net for a UAV target location name (e.g. "target alpha" vs. bare "alpha") -
        // see KnownPoints.CanonicalizeText's own doc comment for why this has to be applied here,
        // at the true final choke point, rather than trusted to every agent hop along the way.
        // Referencing a MoavAgent-domain type from this otherwise domain-agnostic entry point is a
        // deliberate, narrow exception: this is the one place common to every branch (MoavAgent or
        // SimulatorAgent) where the operator-facing text is finalized, and the transform is a no-op
        // for any text that never mentions one of these known point names.
        return (KnownPoints.CanonicalizeText(responseText), sw.Elapsed.TotalSeconds);
    }
}

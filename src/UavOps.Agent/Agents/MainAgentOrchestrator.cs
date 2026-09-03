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
/// </summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory, ToolInvocationLogger toolLogger)
{
    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var (brainAgent, session) = await agentFactory.GetOrCreatePersistentBrainAgentAsync(cancellationToken);
        var tools = await agentFactory.BuildRootToolsForTurn(correlationId, text, cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = tools, AllowMultipleToolCalls = true });

        var response = await brainAgent.RunAsync(text, session, runOptions, cancellationToken: cancellationToken);
        sw.Stop();
        toolLogger.ClearInvocationTracking(correlationId);

        // Final safety net for a UAV target location name (e.g. "target alpha" vs. bare "alpha") -
        // see KnownPoints.CanonicalizeText's own doc comment for why this has to be applied here,
        // at the true final choke point, rather than trusted to every agent hop along the way.
        // Referencing a MoavAgent-domain type from this otherwise domain-agnostic entry point is a
        // deliberate, narrow exception: this is the one place common to every branch (MoavAgent or
        // SimulatorAgent) where the operator-facing text is finalized, and the transform is a no-op
        // for any text that never mentions one of these known point names.
        return (KnownPoints.CanonicalizeText(response.Text), sw.Elapsed.TotalSeconds);
    }
}

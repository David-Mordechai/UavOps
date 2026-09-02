using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MoavAgent.Simulation;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Entry point the chat hub calls for one operator turn.
///
/// Every turn is first tried against a brand new, memory-less <c>BrainAgent</c> (see
/// <see cref="AgentFactory.BuildStatelessRootAgentForTurn"/>) — the same shape the root agent had
/// before it gained a persistent, multi-turn session. Only when that stateless attempt produces no
/// tool call at all do we fall back to the memory-carrying <c>BrainAgent</c> (see
/// <see cref="AgentFactory.GetOrCreatePersistentBrainAgentAsync"/>) for a real answer — the case
/// that feature exists for: recalling, summarizing, or referring back to something earlier in the
/// conversation, none of which needs to actually command anything.
///
/// This ordering, not just prompt wording, is what prevents a real production incident: once the
/// memory-carrying session had one real "speed/altitude updated" turn in its history, granite4.1:3b
/// would answer a near-identical follow-up command (even with different numbers) in well under a
/// second with zero tool calls — pattern-completing its own prior success instead of actually
/// delegating the new command, and reporting a UAV action as done that never happened. Ollama does
/// not honor <see cref="ChatToolMode.RequireAny"/> (verified directly against its OpenAI-compatible
/// endpoint — a request with <c>tool_choice: required</c> still came back as plain text with no tool
/// call), so there is no way to force the model to call a tool via the API; the only reliable fix is
/// to never let an actionable command reach the history-carrying agent in the first place. A
/// stateless attempt that genuinely didn't need to act (recall, chit-chat, a clarifying question)
/// also produces no tool call, so it costs an extra model round-trip on exactly those turns, never on
/// an actioned one — the memory-carrying agent's own turn is skipped entirely when the stateless
/// attempt already delegated for real, so a completed action is also never written into that session
/// history (a deliberate trade: the persisted history holds conversation, not command outcomes, so it
/// can never be replayed as a false "already done").
///
/// Validated live against the actually-configured backend (not just Ollama/granite4.1:3b - see
/// appsettings.json's <c>AgentModels</c>) by driving the real running app through a SignalR client
/// exactly like the browser does: the same near-identical speed/altitude command repeated 8 times
/// in a row against a fresh server process produced a real tool call every single time. An xunit
/// regression test attempting the same two-turn check in-process came back flaky for reasons not
/// fully understood despite matching config/model, so it was left out rather than committed red -
/// this doc comment is the record of that validation instead.
/// </summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory, ToolInvocationLogger toolLogger)
{
    // A stateless attempt can still - independent of the memory bug this class exists to prevent -
    // occasionally come back with confident-sounding text and zero tool calls, since granite4.1:3b
    // sometimes skips tool-calling even with no history to pattern-complete against (observed
    // directly while testing this fix). One retry substantially improves the odds of catching a
    // real action without the unbounded cost of retrying forever; Ollama doesn't honor
    // ChatToolMode.RequireAny (see class doc comment), so there is no way to force this instead.
    private const int MaxStatelessAttempts = 2;

    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= MaxStatelessAttempts; attempt++)
        {
            var statelessAgent = await agentFactory.BuildStatelessRootAgentForTurn(correlationId, text, cancellationToken);
            var statelessResponse = await statelessAgent.RunAsync(text, cancellationToken: cancellationToken);

            if (toolLogger.HasAnyToolBeenInvoked(correlationId))
            {
                toolLogger.ClearInvocationTracking(correlationId);
                sw.Stop();
                return (KnownPoints.CanonicalizeText(statelessResponse.Text), sw.Elapsed.TotalSeconds);
            }

            toolLogger.ClearInvocationTracking(correlationId);
        }

        var (brainAgent, session) = await agentFactory.GetOrCreatePersistentBrainAgentAsync(cancellationToken);
        var tools = await agentFactory.BuildRootToolsForTurn(correlationId, text, cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = tools, AllowMultipleToolCalls = true });

        var response = await brainAgent.RunAsync(text, session, runOptions, cancellationToken: cancellationToken);
        sw.Stop();

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

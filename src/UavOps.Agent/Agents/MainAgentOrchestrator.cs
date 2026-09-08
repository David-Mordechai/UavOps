using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UavOps.Agent.Contracts;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Entry point the chat hub calls for one operator turn. Every turn goes straight to the single,
/// long-lived, memory-carrying <c>BrainAgent</c> (see
/// <see cref="AgentFactory.GetOrCreatePersistentBrainAgentAsync"/>), with <c>tool_choice</c> left at
/// its default ("auto") - never forced.
///
/// This used to force <c>tool_choice: required</c> on every turn's first completion
/// (<c>RequireToolOnFirstTurnChatClient</c>, now deleted) plus a 3-attempt verified-retry loop that
/// rolled back BrainAgent's own history and re-tried whenever a completion produced zero tool calls
/// - built after a real, live-reproduced incident where BrainAgent answered an actionable fleet-wide
/// command with a fully fabricated success report and zero tool calls, under the OLD multi-agent
/// delegation architecture (BrainAgent → MoavAgent → per-domain leaf specialists).
///
/// Both removed after direct, repeated evidence that neither is needed - or wanted - for the flat
/// single-agent architecture that replaced that delegation tree. Two independent standalone
/// baselines this session (a pure-Python script over raw HTTP, and a real .NET
/// Microsoft.Agents.AI/Microsoft.Extensions.AI harness matching this app's own construction path -
/// see <c>eval/single-agent-baseline/</c> and <c>eval/single-agent-baseline-dotnet/</c>) both used
/// `tool_choice: "auto"`, never forced, and both scored 8/8 correct runs against this same model on
/// the exact scenario that produced the original fabrication incident - proving the flat,
/// no-delegation architecture itself (not forcing, not a retry loop) is what prevents the
/// fabrication. Forcing was carried over into the flat rewrite defensively, without re-testing
/// whether it was still load-bearing here - it was not: forcing turned out to actively break a
/// *different*, real scenario instead (a bare greeting deterministically failing to produce any
/// tool call at all when forced - confirmed 8/8 via a repeated live regression test - even though
/// the identical flat design with auto-mode tool choice never exhibited this at all). Removing
/// forcing directly fixes that regression, and the fleet-wide fabrication scenario was re-verified
/// live, repeatedly, with forcing/retry both removed, before this was considered safe to ship - see
/// <see cref="FlyAllFleetWideLiveTests.FlyAllFleetWideCommand_NeverClaimsSuccessWithoutRealMutation"/>
/// (now run with a real repeat count, not 1) for the current data.
///
/// What this does NOT change: the deterministic guards that protect against something no amount of
/// model quality fixes - <c>ConfirmationGate</c>/<c>OperatorPromptGate</c> requiring real operator
/// sign-off before a consequential action, <c>TailNumberDisambiguationTool</c>'s grounding of every
/// tail number against the real fleet - are untouched; those were never compensating for
/// unreliability, they're an intentional safety boundary.
/// </summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory, ToolInvocationLogger toolLogger)
{
    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var (brainAgent, session, _) = await agentFactory.GetOrCreatePersistentBrainAgentAsync(cancellationToken);
        var tools = await agentFactory.BuildToolsForTurn(correlationId, text, cancellationToken);
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

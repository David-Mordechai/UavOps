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
///
/// One narrow addition since the above: <see cref="RepeatQuestionReminder"/> - a separate, real,
/// live-reproduced bug where the model answered a status question fabricated from memory instead
/// of calling its tool again, specifically when the operator repeated the exact same question
/// (measured ~1-in-5 even with an explicit "always call this fresh" instruction already in
/// BrainAgent.yaml). Deliberately not a reintroduction of the forcing/retry mechanism above - see
/// that class's own doc comment for why this is safe against the same regression.
/// </summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory, ToolInvocationLogger toolLogger)
{
    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var (brainAgent, session, historyProvider) = await agentFactory.GetOrCreatePersistentBrainAgentAsync(cancellationToken);

        // Tool retrieval needs to see this exchange's real context, not just this turn's bare
        // text - a real, live-reproduced bug: an ambiguous follow-up ("999" answering "which UAV?")
        // has no semantic content of its own, so embedding it alone ranks the whole tool catalog as
        // near-random noise, silently excluding a tool (e.g. SetSpeed) the earlier part of the same
        // exchange actually needed. History is read here (BEFORE this turn's message joins it) and
        // combined with the current text, so retrieval sees the same conversation BrainAgent itself
        // does - not a separately-invented "last N messages" window, but the same bounded history
        // ToolCallAwareChatReducer already keeps for the model. Skips System/Tool-role entries and
        // pure tool-call/tool-result messages (ChatMessage.Text is empty for those) - their content
        // is either the system prompt (irrelevant to, and would dilute, retrieval) or already
        // reflected in the plain-language turns around them.
        var history = historyProvider.GetMessages(session);
        var historyText = history
            .Where(m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant) && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text);
        var retrievalQueryText = string.Join("\n", historyText.Append(text));

        var tools = await agentFactory.BuildToolsForTurn(correlationId, text, retrievalQueryText, cancellationToken);
        var chatOptions = new ChatOptions { Tools = tools, AllowMultipleToolCalls = true };

        // See RepeatQuestionReminder's own doc comment for the real, live-reproduced bug this
        // closes, and why forcing one specific, already-proven tool name here is safe against the
        // regression that made forcing ANY tool on every turn get reverted before (see this class's
        // own doc comment above) - the guard on toolName actually being in this turn's own offered
        // tools list is defensive: retrieval offered it every time this was live-tested, but forcing
        // a tool name the model was never even given would be a hard error, not just a bad guess.
        var isRepeat = RepeatQuestionReminder.TryFindRepeatedToolName(text, history, out var repeatedToolName)
            && tools.Any(t => ((AIFunction)t).Name == repeatedToolName);
        if (isRepeat)
        {
            chatOptions.ToolMode = ChatToolMode.RequireSpecific(repeatedToolName!);
        }

        var runOptions = new ChatClientAgentRunOptions(chatOptions);
        var response = isRepeat
            ? await brainAgent.RunAsync(RepeatQuestionReminder.BuildTurnMessages(text), session, runOptions, cancellationToken: cancellationToken)
            : await brainAgent.RunAsync(text, session, runOptions, cancellationToken: cancellationToken);

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

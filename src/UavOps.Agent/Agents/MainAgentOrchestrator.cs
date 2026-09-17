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
/// of calling its tool again, specifically when the operator repeated the exact same question.
/// Deliberately not a reintroduction of the forcing/retry mechanism above - see that class's own
/// doc comment for why this is safe against the same regression. This is the ONE exception to
/// "domain-agnostic" this file makes, and only because it genuinely is domain-agnostic itself - it
/// never references a specific tool name, parameter name, or domain vocabulary; the exact same
/// mechanism would work unmodified for a Watchdog or Simulator repeat-question bug.
///
/// Two further, Moav-specific bugs were found this same session and deliberately do NOT get a
/// host-side fix, even a "generic-looking" one: (1) a fleet-wide command in one turn followed by a
/// bare-plural-pronoun follow-up in a later turn ("point their payloads there") often failing to
/// resolve to every UAV, and (2) a single compound turn ("fly them to alpha at speed 250 and
/// altitude 3000") occasionally calling its first tool (Navigate) for real, then fabricating a
/// false excuse for skipping the rest. Host-level fixes for both were built, live-tested clean, and
/// then reverted anyway - not because they didn't work, but because both are genuinely,
/// structurally Moav-specific problems (only Moav has "many addressable UAVs" a plural pronoun can
/// refer back to, and only Moav has this specific compound-tool-call shape), and dressing that up
/// in domain-agnostic-sounding regex/keyword checks in THIS file still means the host's own
/// behavior is shaped by one domain's needs - exactly what "Split BrainAgent's 3 domains into
/// separate MCP servers" (see CLAUDE.md) exists to prevent. The MCP protocol gives a domain exactly
/// two channels to influence the model: its own tools' schemas, and its own `serverInstructions`
/// block (folded into the system prompt once at startup) - there is no MCP hook for "inject a
/// per-turn reminder message," so a real per-domain behavioral fix has to be YAML content in that
/// domain's own ToolsConfig.yaml, not C# here, however that constrains the shape the fix can take.
/// Both bugs are fixed this way now - see McpMoav/ToolsConfig.yaml's own serverInstructions.
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
        // closes. `ChatToolMode.RequireSpecific` forcing here is DISABLED as of this session -
        // live-reproduced repeatedly (browser + unit tests, same day) that forcing tool_choice to
        // one specific function on THIS model/vLLM/parser combination (nvfp4-quantized MoE,
        // qwen3_coder tool-call parser, temperature forced to 0 server-side - see
        // src/UavOps.Agent.McpMoav/appsettings or the vLLM container's own startup flags) can make
        // a single completion take 100-900+ seconds: grammar-constrained decoding for a forced
        // function call, combined with greedy (temp=0) sampling, occasionally needs a very large
        // number of low-confidence steps to find a token sequence satisfying both the schema
        // constraint and the argmax path. Every slow/hanging response measured this session -
        // 101s/301s durations in RepeatedFleetQueryLiveTests, a live 901s browser hang - traced
        // back to this exact forced-tool_choice code path; no other turn type (auto tool_choice,
        // including fleet-wide multi-tool turns) ever exhibited it. The injected reminder message
        // (BuildTurnMessages) still runs either way - only the forcing is removed here, to test
        // whether the reminder text alone reliably gets a real tool call without the pathological
        // decoding slowdown. Re-verify with RepeatedFleetQueryLiveTests/PointPayloadFollowUpLiveTests
        // at full repeat count before considering this closed either way.
        var isRepeat = RepeatQuestionReminder.TryFindRepeatedToolName(text, history, out var repeatedToolName)
            && tools.Any(t => ((AIFunction)t).Name == repeatedToolName);

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

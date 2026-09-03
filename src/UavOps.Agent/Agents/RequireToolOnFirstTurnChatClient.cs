using Microsoft.Extensions.AI;

namespace UavOps.Agent.Agents;

/// <summary>
/// Forces <see cref="ChatToolMode.RequireAny"/> on the first completion of *this* turn only -
/// detected by there being no <see cref="ChatRole.Tool"/> message since the most recent
/// <see cref="ChatRole.User"/> message - and passes through whatever <see cref="ChatOptions.ToolMode"/>
/// the caller already set (normally "auto") for every later completion of the same turn, once at
/// least one real tool result exists for it.
///
/// Deliberately scoped to "since the most recent user message", not "anywhere in the message list":
/// a memory-less, single-turn agent (a leaf specialist like <c>FlightControlAgent</c>) only ever sees
/// one user message per call, so those two things are the same for it - but <c>BrainAgent</c> carries
/// a real, growing multi-turn session, where earlier turns' tool messages stay in history forever.
/// Checking "anywhere in the list" would mean this only ever forces once, on the very first message
/// of the whole session - never again for any later turn, which is exactly where the fabrication this
/// class exists to prevent was actually observed.
///
/// This is the fix for an agent whose job is to act via tools answering an unambiguous, actionable
/// instruction with plain text instead of ever calling one, then reporting a false success -
/// observed twice, in two different shapes: a leaf specialist under concurrent multi-UAV delegation
/// (two of three concurrent instances called nothing at all while claiming the same outcome as the
/// one that actually acted), and separately, BrainAgent itself, several turns into a long session
/// with real memory of its own prior successful delegations, answering a new but similar command in
/// well under a second with zero delegation at all - the original stateless-first incident,
/// resurfacing one level up once real multi-turn memory was introduced there.
///
/// Forcing <c>RequireAny</c> for a whole conversation isn't viable - the model would never be
/// allowed to stop calling tools and produce its own final text summary (this is very likely the
/// actual mechanism behind an earlier, cruder attempt at this - see
/// <c>TailNumberProvenanceGuardTool</c>'s own doc comment - being observed to "hang" the backend).
/// Restricting the force to only the first completion of each turn avoids that.
///
/// Applied to leaf/actor agents (no legitimate reason to reply in bare text before calling anything
/// at all - worst case it should call a read-only query like <c>ListFleet</c> to resolve an
/// ambiguity, which still satisfies the requirement without forcing a guess) and to <c>BrainAgent</c>
/// specifically, whose only tool is now <see cref="CreatePlanTool"/> - a plan with zero
/// steps is a completely valid, forceable response to a purely conversational message, so this no
/// longer conflicts with BrainAgent's legitimate need to sometimes reply in plain text (see
/// <see cref="AgentFactory.BuildAgent"/>'s own use of this class). Never applied to any other
/// router-tier agent (<c>MoavAgent</c>, <c>MaintenanceAgent</c>, <c>SimulatorAgent</c>), which still
/// call their own delegates directly and legitimately need to reply in plain text on a first turn
/// (e.g. "that's outside what I handle").
///
/// Verified directly against the deployed backend (`nvidia/Qwen3.6-35B-A3B-NVFP4` via vLLM,
/// `--enable-auto-tool-choice`) before relying on this: a single <c>tool_choice: "required"</c>
/// request correctly forced a complete, correctly-parameterized set of tool calls, and three
/// concurrent such requests - the exact load pattern that produced the leaf-level incident this
/// fixes - all three came back correct.
/// </summary>
public sealed class RequireToolOnFirstTurnChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, ForceRequiredOnFirstTurn(messages, options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, ForceRequiredOnFirstTurn(messages, options), cancellationToken);

    private static ChatOptions? ForceRequiredOnFirstTurn(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options is not { Tools.Count: > 0 })
        {
            return options; // nothing to force a choice among
        }

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var lastUserIndex = -1;
        for (var i = 0; i < messageList.Count; i++)
        {
            if (messageList[i].Role == ChatRole.User)
            {
                lastUserIndex = i;
            }
        }

        var alreadyCalledAToolThisTurn = false;
        for (var i = lastUserIndex + 1; i < messageList.Count; i++)
        {
            if (messageList[i].Role == ChatRole.Tool)
            {
                alreadyCalledAToolThisTurn = true;
                break;
            }
        }

        if (alreadyCalledAToolThisTurn)
        {
            return options;
        }

        var forced = options.Clone();
        forced.ToolMode = ChatToolMode.RequireAny;
        return forced;
    }
}

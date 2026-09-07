using Microsoft.Extensions.AI;

namespace UavOps.Agent.Agents;

/// <summary>
/// Bounds BrainAgent's reused conversation history the way <c>Microsoft.Extensions.AI</c>'s built-in
/// <c>MessageCountingChatReducer</c> was meant to, without that type's own fatal flaw: verified
/// directly against its real shipped source (<c>dotnet/extensions</c>,
/// <c>src/Libraries/Microsoft.Extensions.AI/ChatReduction/MessageCountingChatReducer.cs</c>) that it
/// unconditionally excludes every message containing a <see cref="FunctionCallContent"/> or
/// <see cref="FunctionResultContent"/> from its output, regardless of the target count - not "trim
/// once you exceed N", but "never include a tool call or tool result, ever". Once BrainAgent's
/// persistent, never-recreated session (see
/// <see cref="AgentFactory.GetOrCreatePersistentBrainAgentAsync"/>) ran even one reduction pass, every
/// tool call it had ever made became invisible to the model on the next completion - only its own
/// past plain-text replies survived, still claiming success, with zero surviving evidence any of
/// them were ever backed by a real tool call. Live-reproduced 2026-09-06
/// (<c>eval/single-agent-baseline-dotnet-persistent/</c>, same model/tools/prompt as this app, one
/// never-recreated session using the real <c>MessageCountingChatReducer(40)</c>): round 0 (before any
/// reduction had run) succeeded with real tool calls; every round after that (1-7) scored zero tool
/// calls and a fully fabricated success report, because by then the model's own history contained
/// nothing but "operator asked, I said I did it" pairs with no tool trace at all to distinguish that
/// pattern from a conversation where it never called anything real.
///
/// This reducer keeps the same bound (a target count of the most recent messages, plus the first
/// system message, exactly like the type it replaces) but operates on whole TURNS, never individual
/// messages - a turn is one operator message plus everything the agent produced in response to it
/// (any number of tool-call/tool-result round trips, ending in its final text reply). Turns are kept
/// or dropped as a unit, from the most recent backward, until the running total reaches the target -
/// so a kept turn's tool calls and their results can never be split apart or silently erased while its
/// surrounding text survives. The most recent turn is always kept in full even if it alone exceeds the
/// target, since truncating a turn already displayed to the operator this session risks exactly the
/// context corruption this class exists to prevent.
/// </summary>
public sealed class ToolCallAwareChatReducer : IChatReducer
{
    private readonly int _targetMessageCount;

    public ToolCallAwareChatReducer(int targetMessageCount)
    {
        if (targetMessageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMessageCount), "Target message count must be positive.");
        }

        _targetMessageCount = targetMessageCount;
    }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        ChatMessage? systemMessage = null;
        var turns = new List<List<ChatMessage>>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                systemMessage ??= message;
                continue;
            }

            // A new turn starts at every operator message. Anything before the first one (there
            // should be none once the system message above is peeled off, but a defensive fallback
            // beats silently dropping a message) still needs a home instead of being lost.
            if (message.Role == ChatRole.User || turns.Count == 0)
            {
                turns.Add([]);
            }

            turns[^1].Add(message);
        }

        var keptTurns = new List<List<ChatMessage>>();
        var keptCount = 0;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (keptTurns.Count > 0 && keptCount >= _targetMessageCount)
            {
                break;
            }

            keptTurns.Insert(0, turns[i]);
            keptCount += turns[i].Count;
        }

        var result = new List<ChatMessage>();
        if (systemMessage is not null)
        {
            result.Add(systemMessage);
        }

        foreach (var turn in keptTurns)
        {
            result.AddRange(turn);
        }

        return Task.FromResult<IEnumerable<ChatMessage>>(result);
    }
}

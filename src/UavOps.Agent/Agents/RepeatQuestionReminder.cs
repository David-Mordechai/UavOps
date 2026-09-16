using Microsoft.Extensions.AI;

namespace UavOps.Agent.Agents;

/// <summary>
/// Deterministic, narrowly-scoped fix for a real, live-reproduced bug: BrainAgent.yaml's own
/// instructions already say every status/informational question needs a fresh tool call even if
/// asked before (added in an earlier session specifically for this), and the model still
/// fabricated an answer from memory instead of calling <c>GetLinkStatus</c> again ~1 in 5 times
/// when the exact same question was repeated - confirmed via the real tool-invocation log (the
/// tool was genuinely offered every time; this is not a retrieval gap).
///
/// A pure-reminder version of this fix (a transient message telling the model to call the tool
/// again, <c>tool_choice</c> left at "auto") measured 15/15 in later live testing but is still, at
/// bottom, a strongly-worded suggestion the model could in principle still ignore - not an actual
/// guarantee. <see cref="MainAgentOrchestrator"/> now additionally forces
/// <c>ChatOptions.ToolMode = ChatToolMode.RequireSpecific(toolName)</c> for a repeat-detected
/// turn, using the EXACT tool name <see cref="TryFindRepeatedToolName"/> found was actually called
/// last time - a real, protocol-level guarantee that tool gets invoked this turn too, not a
/// suggestion. This is deliberately NOT another version of the general <c>tool_choice: required</c>
/// + verified-retry mechanism <see cref="MainAgentOrchestrator"/>'s own doc comment describes being
/// tried and reverted - that forced EVERY turn's first completion to call ANY tool and broke a
/// real no-tool-needed scenario (a bare greeting). This forces one SPECIFIC named tool, and only
/// for a turn already proven (by the same literal, deterministic text-repeat check) to need
/// exactly that tool, because it needed it last time - it can never fire on a turn with no
/// matching prior tool-using occurrence, which a greeting or a first-time question never has.
///
/// The repeat check itself is a purely literal, deterministic string comparison (case/whitespace/
/// trailing-punctuation insensitive only), not a semantic classification of "is this a status
/// question" (which would risk exactly the kind of over-broad trigger that caused a real
/// regression during development - see <see cref="TryFindRepeatedToolName"/>'s own doc comment). A
/// genuinely different question, or a recall/summary request phrased differently ("what did you
/// just say", "give me a summary"), never matches and is completely unaffected - BrainAgent.yaml's
/// own instructions already correctly allow answering those from history.
/// </summary>
public static class RepeatQuestionReminder
{
    /// <summary>Key set on <see cref="ChatMessage.AdditionalProperties"/> for the reminder message
    /// built by <see cref="BuildTurnMessages"/> - <see cref="AgentFactory.GetOrCreatePersistentBrainAgentAsync"/>'s
    /// own <c>StorageInputRequestMessageFilter</c> checks for this exact key to exclude the
    /// reminder from what gets permanently stored, so it's visible to the model for this one
    /// completion only and never pollutes the real conversation history.</summary>
    public const string TransientMarkerKey = "uavops-repeat-question-reminder";

    private const string ReminderText =
        "[System note, not from the operator] The operator's next message repeats an earlier " +
        "question already asked in this conversation. Treat it as a brand new turn regardless: if " +
        "a tool can answer it, you must call that tool again right now - never reuse, echo, or " +
        "paraphrase a past answer or tool result from memory just because the question looks " +
        "identical to one you already answered. State can have changed since then, and you have " +
        "no way to know it hasn't without checking again.";

    /// <summary>True (with <paramref name="toolName"/> set to the exact tool that was called) when
    /// <paramref name="operatorText"/> is a near-exact repeat (case/whitespace/trailing-punctuation
    /// insensitive only - no paraphrase detection, deliberately, so this never second-guesses
    /// whether two differently-worded questions "mean the same thing") of some earlier real User
    /// turn in <paramref name="history"/> that ALSO actually resulted in exactly one real tool call
    /// that same turn.
    ///
    /// The tool-call condition is required, not just the text match - live-reproduced: without it, a
    /// repeated pure-recall question like "give me a full summary of today's session" (which
    /// legitimately makes zero tool calls, both times, per BrainAgent.yaml's own correct
    /// instructions) matched too, and forcing a tool call for it (this method's caller does exactly
    /// that with the result) made the model invent a UAV-specific status tool call with no tail
    /// number named, triggering an unwanted "which UAV do you mean?" prompt for a question that
    /// never needed a tool at all. Scoping the trigger to "this exact question, asked before, was
    /// answered with exactly one real tool call that time" excludes exactly that case (a summary's
    /// own prior occurrence used no tool, so its repeat is correctly left alone) while still
    /// catching the original bug. Deliberately requires EXACTLY one distinct tool name in that
    /// turn, not "at least one" - a turn that called two or more different tools has no single
    /// unambiguous tool to force via <c>ChatToolMode.RequireSpecific</c>, so it's left to the
    /// reminder-only path instead of guessing which one to force.</summary>
    public static bool TryFindRepeatedToolName(string operatorText, IEnumerable<ChatMessage> history, out string? toolName)
    {
        toolName = null;
        var normalizedCurrent = Normalize(operatorText);
        if (normalizedCurrent.Length == 0)
        {
            return false;
        }

        // Groups history into turns the same way ToolCallAwareChatReducer does (a new turn starts
        // at every User message) so each candidate match can be checked for a real tool call
        // confined to THAT turn, not just anywhere in the whole conversation.
        List<ChatMessage>? currentTurn = null;
        var turns = new List<List<ChatMessage>>();
        foreach (var message in history)
        {
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            if (message.Role == ChatRole.User)
            {
                currentTurn = [];
                turns.Add(currentTurn);
            }

            currentTurn?.Add(message);
        }

        foreach (var turn in turns)
        {
            if (turn.Count == 0 || turn[0].Role != ChatRole.User || string.IsNullOrWhiteSpace(turn[0].Text)
                || Normalize(turn[0].Text) != normalizedCurrent)
            {
                continue;
            }

            var toolNames = turn
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Select(c => c.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (toolNames.Count == 1)
            {
                toolName = toolNames[0];
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the two-message list to pass to <c>AIAgent.RunAsync(IEnumerable&lt;ChatMessage&gt;, ...)</c>
    /// for a turn detected as a repeat: the transient reminder first, then the operator's own text
    /// unchanged - the reminder is filtered out of storage (see <see cref="TransientMarkerKey"/>),
    /// so the persisted history still shows exactly what the operator said, nothing added.
    ///
    /// The reminder is <see cref="ChatRole.User"/>, deliberately NOT <see cref="ChatRole.System"/> -
    /// live-reproduced: the real OpenAI-compatible backend this app talks to rejects outright
    /// (HTTP 400 "System message must be at the beginning") any completion request whose system
    /// message isn't the very first message overall, which a per-turn system message never is once
    /// BrainAgent's own real system instructions are already the first message in the persisted
    /// session history - every repeat-detected turn failed the underlying completion entirely with
    /// the first version of this fix, which is a strictly worse outcome (a hard error) than the
    /// fabrication bug this exists to reduce.</summary>
    public static List<ChatMessage> BuildTurnMessages(string operatorText)
    {
        var reminder = new ChatMessage(ChatRole.User, ReminderText)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [TransientMarkerKey] = true }
        };
        return [reminder, new ChatMessage(ChatRole.User, operatorText)];
    }

    private static string Normalize(string text) => text.Trim().TrimEnd('.', '!', '?', ' ').ToLowerInvariant();
}

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace UavOps.Agent.Agents;

/// <summary>
/// Things that happened outside a turn - usually a message the operator was sent without asking -
/// waiting to be added to BrainAgent's conversation history, so a follow-up about them has
/// something to refer to. Domain-agnostic: an MCP server decides the words and posts them through
/// <c>ChatHub.PostOperatorMessage</c>/<c>AddHistoryNote</c>. Drained by
/// <see cref="MainAgentOrchestrator.HandleAsync"/> at the start of the next turn rather than
/// written straight into the history, which could race a completion still running.
/// </summary>
public sealed class ProactiveHistoryJournal
{
    /// <summary>Marks the "user" half of an entry, which the operator never typed.</summary>
    public const string NotePrefix = "[System note, not from the operator] ";

    private readonly ConcurrentQueue<(string Note, string Message)> _pending = new();

    /// <param name="note">What happened, for the model (becomes a user-role message).</param>
    /// <param name="message">Exactly what the operator was shown (becomes the assistant reply).</param>
    public void Add(string note, string message) => _pending.Enqueue((note, message));

    /// <summary>Everything pending, oldest first, as user/assistant message pairs - one whole
    /// turn each, so <see cref="Tooling.ToolCallAwareChatReducer"/> keeps or drops each as a unit.</summary>
    public List<ChatMessage> Drain()
    {
        var messages = new List<ChatMessage>();
        while (_pending.TryDequeue(out var entry))
        {
            messages.Add(new ChatMessage(ChatRole.User, NotePrefix + entry.Note));
            messages.Add(new ChatMessage(ChatRole.Assistant, entry.Message));
        }
        return messages;
    }
}

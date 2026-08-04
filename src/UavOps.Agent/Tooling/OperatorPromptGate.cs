using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Hubs;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Chat-driven "ask the operator an open question and read back their choice" gate — the
/// open-ended counterpart to <see cref="ConfirmationGate"/> (which only ever resolves yes/no).
/// Used by bespoke tools like <see cref="UavOps.Agent.Agents.AskOperatorChoiceTool"/> that need a
/// free-text answer matched against a short list of valid choices.
///
/// Same in-chat round-trip mechanics as <see cref="ConfirmationGate"/>: the question and its
/// resolution are sent as ordinary <c>ReceiveChatMessage</c> events (under their own
/// correlationId so they render as their own bubble), and the operator's next plain-text reply is
/// matched against the offered choices by <see cref="Hubs.ChatHub.SendMessage"/> before being
/// treated as a new command.
///
/// Independent turnstile from <see cref="ConfirmationGate"/> (its own <see cref="_turnstile"/>) —
/// a known, accepted limitation is that if a confirmation and an operator-choice prompt were ever
/// pending at the same time, a bare reply could be ambiguous about which one it answers. Not
/// expected in practice: the simulator lesson flow's own gated tools are strictly sequential
/// (list lessons -> ask which one -> confirm-and-run), never concurrent with each other.
/// </summary>
public sealed class OperatorPromptGate(
    IHubContext<ChatHub> hub,
    ILogger<OperatorPromptGate> logger,
    TimeSpan? timeout = null)
{
    // Longer than ConfirmationGate's 60s default — reading a list of options and choosing one
    // takes longer than a yes/no decision.
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(120);
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private volatile PendingPrompt? _current;

    private sealed record PendingPrompt(
        string AgentName, string Question, IReadOnlyList<string> Choices, string PromptCorrelationId, TaskCompletionSource<string?> Tcs);

    public async Task<string?> RequestChoiceAsync(
        string correlationId,
        string agentName,
        string question,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken);
        try
        {
            var promptCorrelationId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = new PendingPrompt(agentName, question, choices, promptCorrelationId, tcs);

            var optionsList = string.Join(", ", choices);
            await hub.Clients.All.SendAsync(
                "ReceiveChatMessage",
                agentName,
                $"{question.TrimEnd('.')}. Options: {optionsList}. Reply with the name (or number) of your choice (within {_timeout.TotalSeconds:0}s).",
                0d,
                promptCorrelationId,
                cancellationToken);

            var sw = Stopwatch.StartNew();

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var registration = linked.Token.Register(() => tcs.TrySetResult(null));

            var choice = await tcs.Task;
            sw.Stop();

            logger.LogInformation(
                "[OperatorPrompt] correlationId={CorrelationId} agent={Agent} -> {Outcome}",
                correlationId, agentName, choice ?? "no valid choice / timed out");

            await hub.Clients.All.SendAsync(
                "ReceiveChatMessage",
                agentName,
                choice is not null
                    ? $"Got it — proceeding with: {choice}."
                    : $"No valid choice received within {_timeout.TotalSeconds:0}s — not proceeding.",
                sw.Elapsed.TotalSeconds,
                promptCorrelationId,
                CancellationToken.None);

            return choice;
        }
        finally
        {
            _current = null;
            _turnstile.Release();
        }
    }

    /// <summary>
    /// Called by <see cref="Hubs.ChatHub.SendMessage"/> for every incoming operator message,
    /// alongside <see cref="ConfirmationGate.TryHandleChatReplyAsync"/>, before it's treated as a
    /// new command. Returns true if the message was consumed as a reply to a pending prompt
    /// (whether or not it matched a valid choice) — the caller should stop processing that
    /// message either way.
    /// </summary>
    public async Task<bool> TryHandleChatReplyAsync(string text, CancellationToken cancellationToken)
    {
        var current = _current;
        if (current is null)
        {
            return false;
        }

        var match = MatchChoice(text, current.Choices);
        if (match is null)
        {
            await hub.Clients.All.SendAsync(
                "ReceiveChatMessage",
                current.AgentName,
                $"Sorry, I didn't recognize that choice. Still waiting: {current.Question.TrimEnd('.')}. " +
                $"Options: {string.Join(", ", current.Choices)}.",
                0d,
                current.PromptCorrelationId,
                cancellationToken);
            return true;
        }

        current.Tcs.TrySetResult(match);
        return true;
    }

    /// <summary>Matches a reply against the offered choices — an exact (case-insensitive) name
    /// match, or a 1-based index into the list. Deliberately not an LLM classification, same
    /// reasoning as <see cref="ChatConfirmationParser"/>: the operator's choice drives which
    /// script actually runs, so the match needs to be deterministic and auditable.</summary>
    private static string? MatchChoice(string text, IReadOnlyList<string> choices)
    {
        var trimmed = text.Trim();

        if (int.TryParse(trimmed, out var index) && index >= 1 && index <= choices.Count)
        {
            return choices[index - 1];
        }

        return choices.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

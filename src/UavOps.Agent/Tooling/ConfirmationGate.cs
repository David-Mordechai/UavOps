using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Config-switchable, chat-driven confirmation gate. A tool call only goes through this when its
/// <see cref="AgentToolConfig.RequiresConfirmation"/> is true (an opt-in, per-tool setting — see
/// <see cref="OperationTool"/>) and <see cref="ExecutionMode"/> is <c>Confirm</c>;
/// ExecutionMode reads live from IConfiguration so flipping it in appsettings.json takes effect
/// without a restart.
///
/// The approval round-trip happens as an ordinary chat message, not a UI card: the prompt and its
/// resolution are sent as <c>ReceiveChatMessage</c> events (under a correlationId of their own, so
/// they render as their own bubble rather than overwriting the turn that triggered them), and the
/// operator's next plain-text reply is parsed by <see cref="ChatConfirmationParser"/> — see
/// <see cref="ChatHub.SendMessage"/>, which checks <see cref="TryHandleChatReplyAsync"/> before
/// treating an incoming message as a new command. The prompt text is built from the tool's
/// human-authored <see cref="AgentToolConfig.Description"/>, never the raw operationId or JSON
/// arguments — an operator shouldn't need to know function names to approve or decline an action.
///
/// Only one confirmation can be outstanding at a time (<see cref="_turnstile"/>) — with
/// concurrent tool invocation enabled, two mutating calls could otherwise both need approval at
/// once, and a single free-text "yes" from the operator would be ambiguous about which one it
/// answers. Serializing keeps the chat exchange a normal, unambiguous back-and-forth.
/// </summary>
public sealed class ConfirmationGate(
    IHubContext<ChatHub> hub,
    IConfiguration configuration,
    ILogger<ConfirmationGate> logger,
    TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private volatile PendingConfirmation? _current;

    private sealed record PendingConfirmation(string AgentName, string Summary, string PromptCorrelationId, TaskCompletionSource<bool> Tcs);

    public ExecutionMode CurrentMode =>
        Enum.TryParse<ExecutionMode>(configuration["ExecutionMode"], ignoreCase: true, out var mode)
            ? mode
            : ExecutionMode.Confirm; // fail safe: default to requiring confirmation if misconfigured

    public async Task<bool> RequireConfirmationAsync(
        string correlationId,
        string agentName,
        string operationId,
        string description,
        object? arguments,
        CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken);
        try
        {
            var promptCorrelationId = Guid.NewGuid().ToString("N")[..8];
            var summary = $"{description.TrimEnd('.')} ({FormatArguments(arguments)})";
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = new PendingConfirmation(agentName, summary, promptCorrelationId, tcs);

            await hub.Clients.All.SendAsync(
                "ReceiveChatMessage",
                agentName,
                $"Approval needed: {summary}. Reply \"yes\" to approve or \"no\" to decline (within {_timeout.TotalSeconds:0}s).",
                0d,
                promptCorrelationId,
                cancellationToken);

            var sw = Stopwatch.StartNew();

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var registration = linked.Token.Register(() => tcs.TrySetResult(false));

            var approved = await tcs.Task;
            sw.Stop();

            logger.LogInformation(
                "[Confirmation] correlationId={CorrelationId} agent={Agent} tool={Tool} -> {Outcome}",
                correlationId, agentName, operationId, approved ? "approved" : "declined/timed out");

            await hub.Clients.All.SendAsync(
                "ReceiveChatMessage",
                agentName,
                approved
                    ? $"Approved — proceeding with: {summary}."
                    : $"Declined (no reply within {_timeout.TotalSeconds:0}s) — not executed: {summary}.",
                sw.Elapsed.TotalSeconds,
                promptCorrelationId,
                CancellationToken.None);

            return approved;
        }
        finally
        {
            _current = null;
            _turnstile.Release();
        }
    }

    /// <summary>
    /// Called by <see cref="ChatHub.SendMessage"/> for every incoming operator message before it
    /// is treated as a new command. Returns true if the message was consumed as a reply to a
    /// pending confirmation (whether or not it parsed as yes/no) — the caller should stop
    /// processing that message either way.
    /// </summary>
    public async Task<bool> TryHandleChatReplyAsync(string text, CancellationToken cancellationToken)
    {
        var current = _current;
        if (current is null)
        {
            return false;
        }

        if (!ChatConfirmationParser.TryParse(text, out var approved))
        {
            await hub.Clients.All.SendAsync(
                "ReceiveChatMessage",
                current.AgentName,
                $"Sorry, I didn't catch that as yes or no. Still waiting on approval: {current.Summary} — reply \"yes\" or \"no\".",
                0d,
                current.PromptCorrelationId,
                cancellationToken);
            return true;
        }

        current.Tcs.TrySetResult(approved);
        return true;
    }

    /// <summary>Renders tool arguments as "name: value, name: value" instead of raw JSON — the
    /// operator sees plain values, not braces/quotes/operationIds. Falls back to the raw JSON
    /// text for non-scalar values (e.g. a waypoint list) rather than trying to prettify those.</summary>
    private static string FormatArguments(object? arguments)
    {
        var element = JsonSerializer.SerializeToElement(arguments);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element.GetRawText();
        }

        var properties = element.EnumerateObject().ToList();
        if (properties.Count == 0)
        {
            return "no arguments";
        }

        return string.Join(", ", properties.Select(p => $"{p.Name}: {FormatValue(p.Value)}"));
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "none",
        _ => value.GetRawText()
    };
}

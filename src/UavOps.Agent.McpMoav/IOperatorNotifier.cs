using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpMoav;

/// <summary>Tells the operator something they didn't ask for (a detection, a finished search).
/// Only the host can reach the chat, so this goes through its generic
/// <see cref="HostHubContract.Methods.PostOperatorMessage"/>; the words are decided here.</summary>
public interface IOperatorNotifier
{
    /// <param name="message">Shown to the operator exactly as given.</param>
    /// <param name="historyNote">What happened, for BrainAgent's history (null: history untouched).</param>
    Task PostAsync(string message, string? historyNote);

    /// <summary>Only BrainAgent's history, nothing shown: for something the operator already knows.</summary>
    /// <param name="note">What happened, for the model.</param>
    /// <param name="message">The reply the history records for it.</param>
    Task AddHistoryNoteAsync(string note, string message);
}

/// <summary>The real notifier, over McpMoav's relay connection to the host (OperationBackend: SignalR).</summary>
public sealed class HubOperatorNotifier(HubConnection hubConnection) : IOperatorNotifier
{
    public Task PostAsync(string message, string? historyNote) =>
        hubConnection.InvokeAsync(HostHubContract.Methods.PostOperatorMessage, message, historyNote);

    public Task AddHistoryNoteAsync(string note, string message) =>
        hubConnection.InvokeAsync(HostHubContract.Methods.AddHistoryNote, note, message);
}

/// <summary>OperationBackend: Simulated has no host connection, and nothing there reports
/// detections either; this only exists so the mission services can be constructed.</summary>
public sealed class LogOnlyOperatorNotifier(ILogger<LogOnlyOperatorNotifier> logger) : IOperatorNotifier
{
    public Task PostAsync(string message, string? historyNote)
    {
        logger.LogInformation("No host connection (OperationBackend: Simulated); operator message not delivered: {Message}", message);
        return Task.CompletedTask;
    }

    public Task AddHistoryNoteAsync(string note, string message)
    {
        logger.LogInformation("No host connection (OperationBackend: Simulated); history note not delivered: {Note}", note);
        return Task.CompletedTask;
    }
}

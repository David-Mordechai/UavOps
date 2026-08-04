using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Agents.MoavAgent.Hubs;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;

namespace UavOps.Agent.Agents.MoavAgent.Operations.Remote;

/// <summary>
/// Adapts the request/response-over-SignalR mechanic <c>ChatHub</c>'s <c>ConfirmationGate</c>
/// uses (send a message to a connected client, await a correlated
/// <see cref="TaskCompletionSource{TResult}"/> with a timeout, resolve it when the client replies)
/// for this different case: multiple operations can be in flight to the connected client at
/// once (unlike <c>ConfirmationGate</c>'s single-turnstile design), so pending replies are keyed
/// by correlation id in a <see cref="ConcurrentDictionary{TKey,TValue}"/> instead of a single
/// field, and messages target one specific connection (<see cref="IHubClients{T}.Client"/>)
/// instead of broadcasting to everyone.
/// </summary>
public sealed class RemoteOperationBroker(
    IHubContext<OperationHub, IOperationClientProxy> hub,
    RemoteOperationOptions options,
    ILogger<RemoteOperationBroker> logger,
    TimeSpan? timeoutOverride = null) : IRemoteOperationBroker
{
    private readonly TimeSpan _timeout = timeoutOverride ?? TimeSpan.FromSeconds(options.TimeoutSeconds);
    private readonly object _connectionLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PendingReply>> _pending = new();
    private volatile string? _connectionId;

    private sealed record PendingReply(bool Success, OperationError Error, string? ErrorMessage, string? ResultJson);

    public void RegisterConnection(string connectionId)
    {
        lock (_connectionLock)
        {
            if (_connectionId is { } previous)
            {
                logger.LogWarning(
                    "Fleet command client {New} connected while {Previous} was still tracked — replacing it (last-writer-wins).",
                    connectionId, previous);
            }
            else
            {
                logger.LogInformation("Fleet command client {ConnectionId} connected.", connectionId);
            }

            _connectionId = connectionId;
        }
    }

    public void UnregisterConnection(string connectionId)
    {
        lock (_connectionLock)
        {
            if (_connectionId != connectionId)
            {
                // Stale disconnect for a connection we've already replaced — don't clobber the
                // newer one's tracked state.
                return;
            }

            _connectionId = null;
        }

        if (_pending.IsEmpty)
        {
            logger.LogInformation("Fleet command client {ConnectionId} disconnected.", connectionId);
            return;
        }

        logger.LogWarning(
            "Fleet command client {ConnectionId} disconnected — failing {Count} in-flight operation(s) immediately.",
            connectionId, _pending.Count);

        foreach (var correlationId in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(correlationId, out var tcs))
            {
                tcs.TrySetResult(new PendingReply(false, OperationError.NoClientConnected, "Fleet command client disconnected.", null));
            }
        }
    }

    public Task Complete(string correlationId, bool success, string? errorMessage, string? resultJson)
    {
        if (_pending.TryGetValue(correlationId, out var tcs))
        {
            tcs.TrySetResult(new PendingReply(success, success ? OperationError.None : OperationError.ClientReportedError, errorMessage, resultJson));
        }

        return Task.CompletedTask;
    }

    public async Task<OperationResult> SendAsync<TResult>(
        Func<IOperationClientProxy, string, Task> invoke,
        CancellationToken cancellationToken)
    {
        var connectionId = _connectionId;
        if (connectionId is null)
        {
            // Fail fast — no correlation id / TCS created, no network round trip, no timeout wait.
            return OperationResult.Fail(OperationError.NoClientConnected, "No fleet command client is connected.");
        }

        var correlationId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<PendingReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            await invoke(hub.Clients.Client(connectionId), correlationId);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(correlationId, out _);
            logger.LogWarning(ex, "Failed to send operation (correlationId={CorrelationId}) to fleet command client.", correlationId);
            return OperationResult.Fail(OperationError.NoClientConnected, "Failed to reach the fleet command client.");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var registration = linked.Token.Register(() =>
            tcs.TrySetResult(new PendingReply(false, OperationError.Timeout, null, null)));

        var reply = await tcs.Task;
        _pending.TryRemove(correlationId, out _);

        if (!reply.Success)
        {
            return OperationResult.Fail(reply.Error, reply.ErrorMessage ?? DefaultMessageFor(reply.Error));
        }

        if (string.IsNullOrEmpty(reply.ResultJson))
        {
            return OperationResult.Ok(null);
        }

        try
        {
            var value = JsonSerializer.Deserialize<TResult>(reply.ResultJson);
            return OperationResult.Ok(value);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Fleet command client returned malformed JSON for correlationId={CorrelationId}.", correlationId);
            return OperationResult.Fail(OperationError.ClientReportedError, "Fleet command client returned an unreadable result.");
        }
    }

    private static string DefaultMessageFor(OperationError error) => error switch
    {
        OperationError.Timeout => "Fleet command client did not respond in time.",
        OperationError.NoClientConnected => "No fleet command client is connected.",
        _ => "Fleet command client reported an error."
    };
}

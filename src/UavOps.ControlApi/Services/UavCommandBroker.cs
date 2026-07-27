using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Options;

namespace UavOps.ControlApi.Services;

/// <summary>
/// Adapts the request/response-over-SignalR mechanic <c>UavOps.Agent</c>'s <c>ConfirmationGate</c>
/// uses (send a message to a connected client, await a correlated
/// <see cref="TaskCompletionSource{TResult}"/> with a timeout, resolve it when the client replies)
/// for this different case: multiple commands can be in flight to the fleet command client at
/// once (unlike <c>ConfirmationGate</c>'s single-turnstile design), so pending replies are keyed
/// by correlation id in a <see cref="ConcurrentDictionary{TKey,TValue}"/> instead of a single
/// field, and messages target one specific connection (<see cref="IHubClients{T}.Client"/>)
/// instead of broadcasting to everyone.
/// </summary>
public sealed class UavCommandBroker(
    IHubContext<UavCommandHub, IUavCommandClientProxy> hub,
    FleetBridgeOptions options,
    ILogger<UavCommandBroker> logger,
    TimeSpan? timeoutOverride = null) : IUavCommandBroker
{
    private readonly TimeSpan _timeout = timeoutOverride ?? TimeSpan.FromSeconds(options.CommandTimeoutSeconds);
    private readonly object _connectionLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PendingReply>> _pending = new();
    private volatile string? _connectionId;

    private sealed record PendingReply(bool Success, BrokerError Error, string? ErrorMessage, string? ResultJson);

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
            "Fleet command client {ConnectionId} disconnected — failing {Count} in-flight command(s) immediately.",
            connectionId, _pending.Count);

        foreach (var correlationId in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(correlationId, out var tcs))
            {
                tcs.TrySetResult(new PendingReply(false, BrokerError.NoClientConnected, "Fleet command client disconnected.", null));
            }
        }
    }

    public Task Complete(string correlationId, bool success, string? errorMessage, string? resultJson)
    {
        if (_pending.TryGetValue(correlationId, out var tcs))
        {
            tcs.TrySetResult(new PendingReply(success, success ? BrokerError.None : BrokerError.ClientReportedError, errorMessage, resultJson));
        }

        return Task.CompletedTask;
    }

    public async Task<BrokerResult<TResult>> SendAsync<TResult>(
        Func<IUavCommandClientProxy, string, Task> invoke,
        CancellationToken cancellationToken)
    {
        var connectionId = _connectionId;
        if (connectionId is null)
        {
            // Fail fast — no correlation id / TCS created, no network round trip, no timeout wait.
            return BrokerResult<TResult>.Fail(BrokerError.NoClientConnected, "No fleet command client is connected.");
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
            logger.LogWarning(ex, "Failed to send command (correlationId={CorrelationId}) to fleet command client.", correlationId);
            return BrokerResult<TResult>.Fail(BrokerError.NoClientConnected, "Failed to reach the fleet command client.");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var registration = linked.Token.Register(() =>
            tcs.TrySetResult(new PendingReply(false, BrokerError.Timeout, null, null)));

        var reply = await tcs.Task;
        _pending.TryRemove(correlationId, out _);

        if (!reply.Success)
        {
            return BrokerResult<TResult>.Fail(reply.Error, reply.ErrorMessage ?? DefaultMessageFor(reply.Error));
        }

        if (string.IsNullOrEmpty(reply.ResultJson))
        {
            return BrokerResult<TResult>.Ok(default!);
        }

        try
        {
            var value = JsonSerializer.Deserialize<TResult>(reply.ResultJson);
            return BrokerResult<TResult>.Ok(value!);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Fleet command client returned malformed JSON for correlationId={CorrelationId}.", correlationId);
            return BrokerResult<TResult>.Fail(BrokerError.ClientReportedError, "Fleet command client returned an unreadable result.");
        }
    }

    private static string DefaultMessageFor(BrokerError error) => error switch
    {
        BrokerError.Timeout => "Fleet command client did not respond in time.",
        BrokerError.NoClientConnected => "No fleet command client is connected.",
        _ => "Fleet command client reported an error."
    };
}

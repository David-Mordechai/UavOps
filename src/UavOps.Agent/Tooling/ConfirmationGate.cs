using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi.Models;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Config-switchable confirmation gate. Read-only operations always execute directly. Mutating
/// operations (POST/PUT/PATCH/DELETE, derived from the OpenAPI verb) go through a
/// approve/decline round-trip with the chat UI when ExecutionMode=Confirm; ExecutionMode reads
/// live from IConfiguration so flipping it in appsettings.json takes effect without a restart.
/// </summary>
public sealed class ConfirmationGate(
    IHubContext<ChatHub> hub,
    IConfiguration configuration,
    ILogger<ConfirmationGate> logger,
    TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

    public ExecutionMode CurrentMode =>
        Enum.TryParse<ExecutionMode>(configuration["ExecutionMode"], ignoreCase: true, out var mode)
            ? mode
            : ExecutionMode.Confirm; // fail safe: default to requiring confirmation if misconfigured

    public static bool IsMutating(OperationType method) =>
        method is OperationType.Post or OperationType.Put or OperationType.Patch or OperationType.Delete;

    public async Task<bool> RequireConfirmationAsync(
        string correlationId,
        string agentName,
        string operationId,
        object? arguments,
        CancellationToken cancellationToken)
    {
        var confirmationId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[confirmationId] = tcs;

        await hub.Clients.All.SendAsync(
            "ReceiveConfirmationRequest",
            confirmationId,
            correlationId,
            agentName,
            operationId,
            JsonSerializer.Serialize(arguments),
            cancellationToken);

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var registration = linked.Token.Register(() => tcs.TrySetResult(false));

        var approved = await tcs.Task;
        _pending.TryRemove(confirmationId, out _);

        logger.LogInformation(
            "[Confirmation] correlationId={CorrelationId} confirmationId={ConfirmationId} agent={Agent} tool={Tool} -> {Outcome}",
            correlationId, confirmationId, agentName, operationId, approved ? "approved" : "declined/timed out");

        return approved;
    }

    public void Resolve(string confirmationId, bool approved)
    {
        if (_pending.TryRemove(confirmationId, out var tcs))
        {
            tcs.TrySetResult(approved);
        }
    }
}

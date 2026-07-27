using Microsoft.AspNetCore.SignalR;
using UavOps.ControlApi.Services;

namespace UavOps.ControlApi.Hubs;

/// <summary>
/// The realtime surface a fleet-commanding app (the real .NET Framework app, or
/// <c>UavOps.MockFleetClient</c> standing in for it during dev) connects to as a SignalR client.
/// Adapts the same request/response-over-SignalR mechanic <c>UavOps.Agent</c>'s
/// <c>ConfirmationGate</c>/<c>ChatHub</c> use for the browser chat client — send a message to the
/// connected client, wait on a correlated <see cref="TaskCompletionSource{TResult}"/> with a
/// timeout — adapted here for multiple concurrent in-flight commands targeting one specific
/// connection, rather than a single broadcast confirmation. All the waiting/matching logic lives
/// in <see cref="IUavCommandBroker"/>; this Hub only tracks the connection and relays the reply.
/// </summary>
public sealed class UavCommandHub(IUavCommandBroker broker) : Hub<IUavCommandClientProxy>
{
    public override Task OnConnectedAsync()
    {
        broker.RegisterConnection(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broker.UnregisterConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Called by the connected client once it has handled a command — resolves the
    /// broker's pending call for that <paramref name="correlationId"/>.</summary>
    public Task SubmitCommandResult(string correlationId, bool success, string? errorMessage, string? resultJson) =>
        broker.Complete(correlationId, success, errorMessage, resultJson);
}

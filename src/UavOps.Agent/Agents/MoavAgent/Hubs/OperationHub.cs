using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Agents.MoavAgent.Operations.Remote;

namespace UavOps.Agent.Agents.MoavAgent.Hubs;

/// <summary>
/// The realtime surface a fleet-commanding app (the real .NET Framework app, or
/// <c>UavOps.MockFleetClient</c> standing in for it during dev) connects to as a SignalR client.
/// Adapts the same request/response-over-SignalR mechanic <c>ChatHub</c>'s <c>ConfirmationGate</c>
/// uses for the browser chat client — send a message to the connected client, wait on a
/// correlated <see cref="TaskCompletionSource{TResult}"/> with a timeout — adapted here for
/// multiple concurrent in-flight operations targeting one specific connection, rather than a
/// single broadcast confirmation. All the waiting/matching logic lives in
/// <see cref="IRemoteOperationBroker"/>; this Hub only tracks the connection and relays the reply.
/// </summary>
public sealed class OperationHub(IRemoteOperationBroker broker) : Hub<IOperationClientProxy>
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

    /// <summary>Called by the connected client once it has handled an operation — resolves the
    /// broker's pending call for that <paramref name="correlationId"/>.</summary>
    public Task SubmitCommandResult(string correlationId, bool success, string? errorMessage, string? resultJson) =>
        broker.Complete(correlationId, success, errorMessage, resultJson);
}

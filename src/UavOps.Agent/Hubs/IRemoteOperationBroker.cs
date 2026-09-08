using UavOps.Agent.Contracts;

namespace UavOps.Agent.Hubs;

/// <summary>
/// Bridges a fleet operation to the connected fleet-commanding client (the real .NET Framework
/// app, or <c>UavOps.MockFleetClient</c>) over <see cref="ChatHub"/>, and back. Called both by
/// <see cref="ChatHub"/>'s own <c>Relay*</c> methods (invoked by <c>UavOps.Agent.McpMoav</c>'s own
/// SignalR client under <c>OperationBackend: SignalR</c>) and by <see cref="ChatHub"/> itself for
/// connection tracking. See <see cref="RemoteOperationBroker"/> for the mechanic.
/// </summary>
public interface IRemoteOperationBroker
{
    void RegisterConnection(string connectionId);
    void UnregisterConnection(string connectionId);

    /// <summary>Called by <see cref="ChatHub.SubmitCommandResult"/> when the client answers.</summary>
    Task Complete(string correlationId, bool success, string? errorMessage, string? resultJson);

    /// <summary>
    /// Sends one operation to the connected client and waits for its reply, deserializing a
    /// successful reply's JSON payload as <typeparamref name="TResult"/> (boxed into
    /// <see cref="OperationResult.Value"/>). <paramref name="invoke"/> is handed the client
    /// proxy and a freshly generated correlation id — call the one typed method on
    /// <see cref="IOperationClientProxy"/> that corresponds to the operation being sent.
    /// </summary>
    Task<OperationResult> SendAsync<TResult>(
        Func<IOperationClientProxy, string, Task> invoke,
        CancellationToken cancellationToken);
}

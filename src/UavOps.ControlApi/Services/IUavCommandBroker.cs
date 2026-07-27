using UavOps.ControlApi.Hubs;

namespace UavOps.ControlApi.Services;

public enum BrokerError
{
    None,
    NoClientConnected,
    Timeout,
    ClientReportedError
}

public readonly record struct BrokerResult<T>(bool Success, BrokerError Error, string? ErrorMessage, T? Value)
{
    public static BrokerResult<T> Ok(T value) => new(true, BrokerError.None, null, value);
    public static BrokerResult<T> Fail(BrokerError error, string? errorMessage) => new(false, error, errorMessage, default);
}

/// <summary>
/// Bridges a synchronous <c>IUavFleetService</c>/<c>IGdtService</c> call to the connected fleet
/// command client (the real .NET Framework app, or <c>UavOps.MockFleetClient</c>) over
/// <see cref="UavCommandHub"/>, and back. See <see cref="UavCommandBroker"/> for the mechanic.
/// </summary>
public interface IUavCommandBroker
{
    void RegisterConnection(string connectionId);
    void UnregisterConnection(string connectionId);

    /// <summary>Called by <see cref="UavCommandHub.SubmitCommandResult"/> when the client answers.</summary>
    Task Complete(string correlationId, bool success, string? errorMessage, string? resultJson);

    /// <summary>
    /// Sends one command to the connected client and waits for its reply. <paramref name="invoke"/>
    /// is handed the client proxy and a freshly generated correlation id — call the one typed
    /// method on <see cref="IUavCommandClientProxy"/> that corresponds to the command being sent.
    /// </summary>
    Task<BrokerResult<TResult>> SendAsync<TResult>(
        Func<IUavCommandClientProxy, string, Task> invoke,
        CancellationToken cancellationToken);
}

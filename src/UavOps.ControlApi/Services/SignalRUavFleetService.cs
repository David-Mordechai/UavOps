using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

/// <summary>
/// <see cref="IUavFleetService"/> backed by <see cref="IUavCommandBroker"/> instead of in-memory
/// state — relays each call to the connected fleet command client over SignalR and blocks on the
/// reply. Blocking (<c>.GetAwaiter().GetResult()</c>) is required because this interface, and the
/// controllers that depend on it, are synchronous; safe under Kestrel (no captured
/// <see cref="SynchronizationContext"/>), at the cost of a thread-pool thread held for up to the
/// broker's timeout per call.
///
/// Broker failures (no client connected, timeout, client-reported error) are logged here at
/// <see cref="LogLevel.Warning"/> with the real reason, then mapped into whatever failure shape
/// this interface's return type allows. For the tuple-returning methods with no error-message
/// slot (<see cref="ListFleet"/>, <see cref="UploadWaypoints"/>, <see cref="GetMissionStatus"/>)
/// that means the HTTP caller only sees a generic "not found"/empty result — a pre-existing
/// limitation of those shapes, not something this backend introduces.
/// </summary>
public sealed class SignalRUavFleetService(IUavCommandBroker broker, ILogger<SignalRUavFleetService> logger) : IUavFleetService
{
    public IReadOnlyCollection<UavSummary> ListFleet()
    {
        var result = Send<List<UavSummary>>((proxy, correlationId) => proxy.ListFleet(correlationId));
        if (!result.Success)
        {
            LogFailure(nameof(ListFleet), null, result);
            return [];
        }

        return result.Value ?? [];
    }

    public UavCommandResult GetTelemetry(string tailNumber) =>
        ToCommandResult(nameof(GetTelemetry), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.GetTelemetry(correlationId, tailNumber)));

    public UavCommandResult Navigate(string tailNumber, string location) =>
        ToCommandResult(nameof(Navigate), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.Navigate(correlationId, tailNumber, location)));

    public UavCommandResult SetSpeed(string tailNumber, int speedKts) =>
        ToCommandResult(nameof(SetSpeed), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.SetSpeed(correlationId, tailNumber, speedKts)));

    public UavCommandResult SetAltitude(string tailNumber, int altitudeFt) =>
        ToCommandResult(nameof(SetAltitude), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.SetAltitude(correlationId, tailNumber, altitudeFt)));

    public UavCommandResult ReturnToLaunch(string tailNumber) =>
        ToCommandResult(nameof(ReturnToLaunch), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.ReturnToLaunch(correlationId, tailNumber)));

    public UavCommandResult PointPayload(string tailNumber, string location) =>
        ToCommandResult(nameof(PointPayload), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.PointPayload(correlationId, tailNumber, location)));

    public UavCommandResult ResetPayload(string tailNumber) =>
        ToCommandResult(nameof(ResetPayload), tailNumber,
            Send<TelemetrySnapshot>((proxy, correlationId) => proxy.ResetPayload(correlationId, tailNumber)));

    public (bool Found, int Accepted) UploadWaypoints(string tailNumber, List<Waypoint> waypoints)
    {
        var result = Send<int>((proxy, correlationId) => proxy.UploadWaypoints(correlationId, tailNumber, waypoints));
        if (!result.Success)
        {
            LogFailure(nameof(UploadWaypoints), tailNumber, result);
            return (false, 0);
        }

        return (true, result.Value);
    }

    public (bool Found, MissionStatus? Status) GetMissionStatus(string tailNumber)
    {
        var result = Send<MissionStatus>((proxy, correlationId) => proxy.GetMissionStatus(correlationId, tailNumber));
        if (!result.Success)
        {
            LogFailure(nameof(GetMissionStatus), tailNumber, result);
            return (false, null);
        }

        return (true, result.Value);
    }

    private BrokerResult<T> Send<T>(Func<IUavCommandClientProxy, string, Task> invoke) =>
        broker.SendAsync<T>(invoke, CancellationToken.None).GetAwaiter().GetResult();

    private UavCommandResult ToCommandResult(string operation, string tailNumber, BrokerResult<TelemetrySnapshot> result)
    {
        if (result.Success)
        {
            return UavCommandResult.Ok(result.Value!);
        }

        LogFailure(operation, tailNumber, result);
        return UavCommandResult.Invalid(result.ErrorMessage ?? "Fleet command client did not respond.");
    }

    private void LogFailure<T>(string operation, string? tailNumber, BrokerResult<T> result) =>
        logger.LogWarning(
            "{Operation}({TailNumber}) failed via the fleet command bridge: {Error} {Message}",
            operation, tailNumber, result.Error, result.ErrorMessage);
}

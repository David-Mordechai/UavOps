using UavOps.Agent.Agents.MoavAgent.Hubs;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Agents.MoavAgent.Operations.Remote;

/// <summary>
/// <see cref="IOperationService"/> backed by <see cref="IRemoteOperationBroker"/> instead of
/// in-memory state — relays each call to the connected fleet-commanding client over SignalR and
/// awaits the reply. Broker failures (no client connected, timeout, client-reported error) are
/// logged here at <see cref="LogLevel.Warning"/> with the real reason; the caller (see
/// <c>Tooling/OperationTool.cs</c>) reports the same failure to the model via
/// <see cref="OperationResult.ErrorMessage"/> — no failure information is lost the way it was
/// under the old REST-controller-driven split interfaces this replaced.
/// </summary>
public sealed class RemoteOperationService(IRemoteOperationBroker broker, ILogger<RemoteOperationService> logger) : IOperationService
{
    public Task<OperationResult> ListFleet(CancellationToken cancellationToken) =>
        Send<List<UavSummary>>(nameof(ListFleet), null, (proxy, correlationId) => proxy.ListFleet(correlationId), cancellationToken);

    public Task<OperationResult> GetTelemetry(string tailNumber, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(GetTelemetry), tailNumber, (proxy, correlationId) => proxy.GetTelemetry(correlationId, tailNumber), cancellationToken);

    public Task<OperationResult> Navigate(string tailNumber, string location, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(Navigate), tailNumber, (proxy, correlationId) => proxy.Navigate(correlationId, tailNumber, location), cancellationToken);

    public Task<OperationResult> SetSpeed(string tailNumber, int speedKts, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(SetSpeed), tailNumber, (proxy, correlationId) => proxy.SetSpeed(correlationId, tailNumber, speedKts), cancellationToken);

    public Task<OperationResult> SetAltitude(string tailNumber, int altitudeFt, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(SetAltitude), tailNumber, (proxy, correlationId) => proxy.SetAltitude(correlationId, tailNumber, altitudeFt), cancellationToken);

    public Task<OperationResult> ReturnToLaunch(string tailNumber, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(ReturnToLaunch), tailNumber, (proxy, correlationId) => proxy.ReturnToLaunch(correlationId, tailNumber), cancellationToken);

    public Task<OperationResult> PointPayload(string tailNumber, string location, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(PointPayload), tailNumber, (proxy, correlationId) => proxy.PointPayload(correlationId, tailNumber, location), cancellationToken);

    public Task<OperationResult> ResetPayload(string tailNumber, CancellationToken cancellationToken) =>
        Send<TelemetrySnapshot>(nameof(ResetPayload), tailNumber, (proxy, correlationId) => proxy.ResetPayload(correlationId, tailNumber), cancellationToken);

    public Task<OperationResult> UploadWaypoints(string tailNumber, List<Waypoint> waypoints, CancellationToken cancellationToken) =>
        Send<int>(nameof(UploadWaypoints), tailNumber, (proxy, correlationId) => proxy.UploadWaypoints(correlationId, tailNumber, waypoints), cancellationToken);

    public Task<OperationResult> GetMissionStatus(string tailNumber, CancellationToken cancellationToken) =>
        Send<MissionStatus>(nameof(GetMissionStatus), tailNumber, (proxy, correlationId) => proxy.GetMissionStatus(correlationId, tailNumber), cancellationToken);

    public Task<OperationResult> GetLinkStatus(string tailNumber, CancellationToken cancellationToken) =>
        Send<GdtLinkStatus>(nameof(GetLinkStatus), tailNumber, (proxy, correlationId) => proxy.GetLinkStatus(correlationId, tailNumber), cancellationToken);

    public Task<OperationResult> SetTrackingMode(string tailNumber, string mode, CancellationToken cancellationToken) =>
        Send<GdtLinkStatus>(nameof(SetTrackingMode), tailNumber, (proxy, correlationId) => proxy.SetTrackingMode(correlationId, tailNumber, mode), cancellationToken);

    private async Task<OperationResult> Send<T>(
        string operation, string? tailNumber, Func<IOperationClientProxy, string, Task> invoke, CancellationToken cancellationToken)
    {
        var result = await broker.SendAsync<T>(invoke, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning(
                "{Operation}({TailNumber}) failed via the fleet command bridge: {Error} {Message}",
                operation, tailNumber, result.Error, result.ErrorMessage);
        }

        return result;
    }
}

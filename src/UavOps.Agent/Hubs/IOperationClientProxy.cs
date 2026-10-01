using UavOps.Agent.Contracts;

namespace UavOps.Agent.Hubs;

/// <summary>
/// The typed server-to-client contract for <see cref="ChatHub"/>'s fleet-commanding-client half —
/// one method per operation, mirroring <c>IOperationService</c> exactly, with a
/// <paramref name="correlationId"/> the client must echo back via
/// <see cref="ChatHub.SubmitCommandResult"/> so the broker can match the reply to the right
/// in-flight call (see <see cref="IRemoteOperationBroker"/>). Method names/signatures here must
/// stay byte-for-byte stable — <c>UavOps.FleetClient</c> (a separate .NET Framework 4.7 project the
/// real fleet-commanding app references) registers handlers against these exact names.
/// </summary>
public interface IOperationClientProxy
{
    Task ListFleet(string correlationId);
    Task GetTelemetry(string correlationId, string tailNumber);
    Task Navigate(string correlationId, string tailNumber, string location);
    Task SetSpeed(string correlationId, string tailNumber, int speedKts);
    Task SetAltitude(string correlationId, string tailNumber, int altitudeFt);
    Task ReturnToLaunch(string correlationId, string tailNumber);
    Task PointPayload(string correlationId, string tailNumber, string location);
    Task ResetPayload(string correlationId, string tailNumber);
    Task UploadWaypoints(string correlationId, string tailNumber, List<Waypoint> waypoints);
    Task GetMissionStatus(string correlationId, string tailNumber);
    Task GetLinkStatus(string correlationId, string tailNumber);
    Task SetTrackingMode(string correlationId, string tailNumber, string mode);

    // AOI search missions. A fleet app opts into these via UavOps.FleetClient's optional
    // IUavMissionHandler; FleetClientConnection fails them fast for one that hasn't.
    Task StartMission(string correlationId, string tailNumber);
    Task StopMission(string correlationId, string tailNumber);
    Task SetSearchTarget(string correlationId, string tailNumber, SearchTargetRequest request);

    // Payload zoom: UavOps.FleetClient's optional IUavPayloadZoomHandler, failed fast the same way.
    Task SetPayloadZoom(string correlationId, string tailNumber, double zoom);
}

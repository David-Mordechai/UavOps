using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Hubs;

/// <summary>
/// The typed server-to-client contract for <see cref="UavCommandHub"/> — one method per fleet
/// command, mirroring <c>IUavFleetService</c>/<c>IGdtService</c> exactly, with a
/// <paramref name="correlationId"/> the client must echo back via
/// <see cref="UavCommandHub.SubmitCommandResult"/> so the broker can match the reply to the
/// right in-flight call (see <see cref="UavOps.ControlApi.Services.IUavCommandBroker"/>).
/// </summary>
public interface IUavCommandClientProxy
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
}

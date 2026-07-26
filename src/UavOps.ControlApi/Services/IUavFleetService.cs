using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

/// <summary>
/// Commands and queries a fleet of UAVs, each addressed by tail number. This is the seam a real
/// implementation (talking to actual vehicles/telemetry) is expected to replace later — callers
/// only depend on this interface, never on how commands are actually carried out.
/// </summary>
public interface IUavFleetService
{
    IReadOnlyCollection<UavSummary> ListFleet();
    UavCommandResult GetTelemetry(string tailNumber);
    UavCommandResult Navigate(string tailNumber, string location);
    UavCommandResult SetSpeed(string tailNumber, int speedKts);
    UavCommandResult SetAltitude(string tailNumber, int altitudeFt);
    UavCommandResult ReturnToLaunch(string tailNumber);
    UavCommandResult PointPayload(string tailNumber, string location);
    UavCommandResult ResetPayload(string tailNumber);
    (bool Found, int Accepted) UploadWaypoints(string tailNumber, List<Waypoint> waypoints);
    (bool Found, MissionStatus? Status) GetMissionStatus(string tailNumber);
}

using System.Collections.Generic;

namespace UavOps.FleetClient
{
    /// <summary>
    /// Implement this to answer fleet commands relayed from UavOps.Agent over its fleet
    /// command hub. Plain, hardware-facing method signatures — no SignalR, JSON, or
    /// correlation-id concerns here; <see cref="FleetClientConnection"/> handles all of that.
    /// This is the one thing a real fleet-commanding application needs to implement to integrate
    /// with UavOps; everything else in this library can be used as-is.
    /// </summary>
    public interface IUavCommandHandler
    {
        CommandResult<List<UavSummary>> ListFleet();
        CommandResult<TelemetrySnapshot> GetTelemetry(string tailNumber);
        CommandResult<TelemetrySnapshot> Navigate(string tailNumber, string location);
        CommandResult<TelemetrySnapshot> SetSpeed(string tailNumber, int speedKts);
        CommandResult<TelemetrySnapshot> SetAltitude(string tailNumber, int altitudeFt);
        CommandResult<TelemetrySnapshot> ReturnToLaunch(string tailNumber);
        CommandResult<TelemetrySnapshot> PointPayload(string tailNumber, string location);
        CommandResult<TelemetrySnapshot> ResetPayload(string tailNumber);
        CommandResult<int> UploadWaypoints(string tailNumber, List<Waypoint> waypoints);
        CommandResult<MissionStatus> GetMissionStatus(string tailNumber);
        CommandResult<GdtLinkStatus> GetLinkStatus(string tailNumber);
        CommandResult<GdtLinkStatus> SetTrackingMode(string tailNumber, string mode);
    }
}

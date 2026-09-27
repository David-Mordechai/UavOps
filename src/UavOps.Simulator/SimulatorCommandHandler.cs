using UavOps.FleetClient;

namespace UavOps.Simulator;

/// <summary>What the real fleet app implements, answered by <see cref="SimFleet"/>.</summary>
public sealed class SimulatorCommandHandler(SimFleet fleet, ILogger<SimulatorCommandHandler> logger) : IUavCommandHandler, IUavMissionHandler, IUavPayloadZoomHandler
{
    public CommandResult<List<UavSummary>> ListFleet() => Log("ListFleet", "", () => CommandResult<List<UavSummary>>.Ok(fleet.ListFleet()));
    public CommandResult<TelemetrySnapshot> GetTelemetry(string tailNumber) => Log("GetTelemetry", tailNumber, () => fleet.GetTelemetry(tailNumber));
    public CommandResult<TelemetrySnapshot> Navigate(string tailNumber, string location) => Log("Navigate", $"{tailNumber}, {location}", () => fleet.Navigate(tailNumber, location));
    public CommandResult<TelemetrySnapshot> SetSpeed(string tailNumber, int speedKts) => Log("SetSpeed", $"{tailNumber}, {speedKts}", () => fleet.SetSpeed(tailNumber, speedKts));
    public CommandResult<TelemetrySnapshot> SetAltitude(string tailNumber, int altitudeFt) => Log("SetAltitude", $"{tailNumber}, {altitudeFt}", () => fleet.SetAltitude(tailNumber, altitudeFt));
    public CommandResult<TelemetrySnapshot> ReturnToLaunch(string tailNumber) => Log("ReturnToLaunch", tailNumber, () => fleet.ReturnToLaunch(tailNumber));
    public CommandResult<TelemetrySnapshot> PointPayload(string tailNumber, string location) => Log("PointPayload", $"{tailNumber}, {location}", () => fleet.PointPayload(tailNumber, location));
    public CommandResult<TelemetrySnapshot> ResetPayload(string tailNumber) => Log("ResetPayload", tailNumber, () => fleet.ResetPayload(tailNumber));
    public CommandResult<TelemetrySnapshot> SetPayloadZoom(string tailNumber, double zoom) => Log("SetPayloadZoom", $"{tailNumber}, {zoom}x", () => fleet.SetPayloadZoom(tailNumber, zoom));
    public CommandResult<int> UploadWaypoints(string tailNumber, List<Waypoint> waypoints) => Log("UploadWaypoints", $"{tailNumber}, {waypoints.Count} waypoints", () => fleet.UploadWaypoints(tailNumber, waypoints));
    public CommandResult<MissionStatus> GetMissionStatus(string tailNumber) => Log("GetMissionStatus", tailNumber, () => fleet.GetMissionStatus(tailNumber));
    public CommandResult<GdtLinkStatus> GetLinkStatus(string tailNumber) => Log("GetLinkStatus", tailNumber, () => fleet.GetLinkStatus(tailNumber));
    public CommandResult<GdtLinkStatus> SetTrackingMode(string tailNumber, string mode) => Log("SetTrackingMode", $"{tailNumber}, {mode}", () => fleet.SetTrackingMode(tailNumber, mode));
    public CommandResult<MissionStatus> StartMission(string tailNumber) => Log("StartMission", tailNumber, () => fleet.StartMission(tailNumber));
    public CommandResult<MissionStatus> SetSearchTarget(string tailNumber, SearchTargetRequest request) =>
        Log("SetSearchTarget", $"{tailNumber}, '{request.Prompt}' in {request.ZoneName}", () => fleet.SetSearchTarget(tailNumber, request));

    private CommandResult<T> Log<T>(string command, string args, Func<CommandResult<T>> run)
    {
        var result = run();
        if (result.Success)
            logger.LogInformation("{Command}({Args})", command, args);
        else
            logger.LogWarning("{Command}({Args}) failed: {Error}", command, args, result.ErrorMessage);
        return result;
    }
}

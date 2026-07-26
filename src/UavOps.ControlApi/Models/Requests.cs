namespace UavOps.ControlApi.Models;

public sealed record NavigateRequest(string Location);
public sealed record SpeedRequest(int SpeedKts);
public sealed record AltitudeRequest(int AltitudeFt);
public sealed record PointPayloadRequest(string Location);
public sealed record UploadWaypointsRequest(List<Waypoint> Waypoints);
public sealed record SetTrackingModeRequest(string Mode);

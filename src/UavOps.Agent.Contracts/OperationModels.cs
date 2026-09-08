namespace UavOps.Agent.Contracts;

public sealed record Waypoint(double Lat, double Lng, int AltitudeFt);

public sealed record TelemetrySnapshot(
    double Lat,
    double Lng,
    int SpeedKts,
    int AltitudeFt,
    string Mode,
    string? PayloadLockedOn);

public sealed record GdtLinkStatus(string LinkState, int SignalStrengthPercent, string TrackingMode);

public sealed record UavSummary(string TailNumber, string Mode, double Lat, double Lng);

public sealed record MissionStatus(string Mode, int WaypointCount);

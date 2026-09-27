namespace UavOps.Agent.Contracts;

public sealed record Waypoint(double Lat, double Lng, int AltitudeFt);

/// <summary>PayloadZoom (1 = widest) and PayloadHfovDeg (the payload camera's horizontal field of
/// view at that zoom) are optional: 0 from a fleet app that doesn't report its payload.</summary>
public sealed record TelemetrySnapshot(
    double Lat,
    double Lng,
    int SpeedKts,
    int AltitudeFt,
    string Mode,
    string? PayloadLockedOn,
    double PayloadZoom = 0,
    double PayloadHfovDeg = 0);

public sealed record GdtLinkStatus(string LinkState, int SignalStrengthPercent, string TrackingMode);

public sealed record UavSummary(string TailNumber, string Mode, double Lat, double Lng);

/// <summary>The trailing fields are optional so a fleet app that predates AOI search missions
/// (and never sets them) still deserializes cleanly.</summary>
public sealed record MissionStatus(
    string Mode,
    int WaypointCount,
    int? CurrentWaypointIndex = null,
    string? ActiveMissionId = null,
    string? SearchPrompt = null);

/// <summary>What the onboard agent should look for while flying a search mission.</summary>
public sealed record SearchTargetRequest(string MissionId, string ZoneName, string Prompt, double MinConfidence);

/// <summary>Sent by the fleet app, unprompted, when the onboard agent spots a search target.</summary>
public sealed record DetectionReport(
    string TailNumber,
    string MissionId,
    string ZoneName,
    string Prompt,
    string Label,
    double Confidence,
    double Lat,
    double Lng,
    DateTime DetectedAtUtc,
    string? TrackId);

/// <summary>Sent by the fleet app, unprompted, when a search mission ends.
/// <see cref="Kind"/> is one of <see cref="MissionEventKinds"/>.</summary>
public sealed record MissionEventReport(string TailNumber, string MissionId, string ZoneName, string Kind);

public static class MissionEventKinds
{
    public const string Completed = "Completed";
    public const string Aborted = "Aborted";
}

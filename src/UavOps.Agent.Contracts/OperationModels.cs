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

/// <summary>What the onboard agent should look for while flying a search mission. With
/// <paramref name="Track"/>, once found it locks on the target and follows it (find and track).
/// With <paramref name="Repeat"/>, the route is flown again and again (a moving target may not be
/// there on one pass) until the mission is stopped.</summary>
public sealed record SearchTargetRequest(string MissionId, string ZoneName, string Prompt, double MinConfidence, bool Track = false, bool Repeat = false);

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

    /// <summary>Find and track: the target is found and locked on; the UAV follows it.</summary>
    public const string Tracking = "Tracking";

    /// <summary>The tracked target hasn't been seen for a while.</summary>
    public const string TargetLost = "TargetLost";

    /// <summary>The tracked target is found again.</summary>
    public const string TargetRegained = "TargetRegained";

    /// <summary>A repeating search flew its whole route once and starts over.</summary>
    public const string PassCompleted = "PassCompleted";

    /// <summary>The tracked target couldn't be found again; the search route is resumed.</summary>
    public const string SearchResumed = "SearchResumed";
}

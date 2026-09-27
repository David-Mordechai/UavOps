namespace UavOps.Agent.Mission;

/// <summary>The "Mission" section of McpMoav's appsettings.json.</summary>
public sealed class MissionOptions
{
    public const string SectionName = "Mission";

    /// <summary>The AOI zone SQLite file. Blank means <see cref="DefaultDatabasePath"/>, which
    /// UavOps.Simulator also defaults to, so both open the same file with no configuration.</summary>
    public string AoiDatabasePath { get; set; } = "";

    public int DefaultAltitudeFt { get; set; } = 1000;
    public double CameraHorizontalFovDeg { get; set; } = 60;
    public double SideOverlap { get; set; } = 0.2;
    public int MaxWaypoints { get; set; } = 300;

    /// <summary>Used for the route's time estimate when the UAV reports no speed.</summary>
    public double DefaultSpeedKts { get; set; } = 100;

    /// <summary>The onboard detector's confidence threshold, sent with a search target.</summary>
    public double MinDetectionConfidence { get; set; } = 0.5;

    /// <summary>A detection of the same target in the same mission within this distance of one
    /// already reported is the same object seen again, and isn't reported twice.</summary>
    public double DetectionDedupeRadiusMeters { get; set; } = 100;

    public static string DefaultDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UavOps", "aoi.db");

    public static string ResolveDatabasePath(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultDatabasePath : Environment.ExpandEnvironmentVariables(configured);
}

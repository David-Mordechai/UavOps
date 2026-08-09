namespace UavOps.Agent.Options;

/// <summary>
/// Which <c>IWatchdogService</c> implementation answers watchdog operations — mirrors
/// <see cref="SimulatorBackend"/>'s role for the simulator domain. Chosen once at startup via the
/// top-level <c>WatchdogBackend</c> config key — see <c>Program.cs</c>.
/// </summary>
public enum WatchdogBackend
{
    /// <summary>In-memory, no watchdog HTTP endpoint or real Windows services required — the
    /// default, same reasoning as <see cref="SimulatorBackend.Fake"/>: most dev machines don't
    /// have the real dependency running, so local dev/testing needs a zero-setup path that still
    /// exercises the full agent/chat/tool flow. Implemented in the separate
    /// <c>UavOps.Agent.Watchdog.Fake</c> project.</summary>
    Fake,

    /// <summary>Polls a real watchdog HTTP health-check endpoint in the background and controls
    /// real Windows services on this machine via the Service Control Manager — see
    /// <see cref="WatchdogOptions"/> for the settings it needs.</summary>
    Real
}

/// <summary>
/// Connection details for the watchdog domain: the HTTP health-check endpoint polled in the
/// background (see <c>Agents.MaintenanceAgent.WatchdogHealthPoller</c>), and the mapping from the
/// watchdog's health-entry names to the real local Windows service names used for
/// start/stop/restart (the two are not the same string — confirmed, not assumed). Only meaningful
/// when <see cref="WatchdogBackend.Real"/> is selected — environment-specific, filled in per
/// deployment, not meaningful defaults.
/// </summary>
public sealed class WatchdogOptions
{
    public const string SectionName = "Watchdog";

    /// <summary>Full URL of the watchdog's HTTP health-check endpoint, returning the standard
    /// ASP.NET Core HealthChecks JSON shape: <c>{ status, entries: { name: { status, description } } }</c>.</summary>
    public string HealthCheckUrl { get; set; } = "";

    /// <summary>How often <c>WatchdogHealthPoller</c> re-polls <see cref="HealthCheckUrl"/>.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Timeout for a single poll HTTP call.</summary>
    public int HttpTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout for a single Start/Stop/Restart Service Control Manager call to reach the
    /// expected status.</summary>
    public int ServiceControlTimeoutSeconds { get; set; } = 30;

    /// <summary>Watchdog health-entry name (the name the model sees, from
    /// <c>IWatchdogService.GetServicesHealth</c>) → real local Windows service name (the SCM
    /// name <c>ServiceController</c> needs). The two don't match 1:1, so Start/Stop/Restart fail
    /// with a clear error for any name missing from this map rather than guessing.</summary>
    public Dictionary<string, string> ServiceNameMap { get; set; } = new();
}

namespace UavOps.Agent.McpWatchdog;

/// <summary>One service's health as last reported by the watchdog — <c>Status</c> is kept as the
/// raw string the watchdog sent (e.g. "Healthy"/"Degraded"/"Unhealthy") rather than an enum, since
/// the watchdog owns that vocabulary and could extend it without this app needing a code change.</summary>
public sealed record ServiceHealthEntry(string Status, string? Description);

/// <summary>One atomic poll result from the watchdog's health endpoint — <see cref="WatchdogHealthStore"/>
/// swaps this in as a single unit so no reader ever observes part of one poll mixed with part of
/// another.</summary>
public sealed record WatchdogHealthSnapshot(
    string OverallStatus,
    IReadOnlyDictionary<string, ServiceHealthEntry> Services,
    DateTimeOffset PolledAt,
    bool Stale);

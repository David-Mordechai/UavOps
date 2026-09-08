namespace UavOps.Agent.McpWatchdog;

public enum ExecutablePathResolutionKind
{
    /// <summary>Every placeholder token was recognized and expanded, and the resulting path exists
    /// on disk.</summary>
    Verified,

    /// <summary>Every placeholder token was recognized and expanded, but the resulting path does
    /// not exist on disk.</summary>
    NotFound,

    /// <summary>The path contains a <c>%Token%</c>-shaped placeholder not present in
    /// <c>WatchdogOptions.ExecutablePlaceholders</c>, so existence can't be verified.</summary>
    Unverifiable
}

public sealed record ExecutablePathResolution(ExecutablePathResolutionKind Kind, string ExpandedPath, string? UnresolvedPlaceholder = null);

/// <summary>Expands the known placeholder tokens (e.g. <c>%MoavProducts%</c>) in a service's
/// <c>executable</c> path using <c>WatchdogOptions.ExecutablePlaceholders</c> and checks whether
/// the fully-expanded path exists on disk — used to verify a service config entry points at a
/// real file before it's written, never to alter what's actually persisted (the config file always
/// keeps the original, unexpanded placeholder form; the watchdog expands these itself at its own
/// runtime). Behind an interface for the same unit-testability reason as
/// <c>IVmwareController</c>/<c>IWindowsServiceController</c>.</summary>
public interface IExecutablePathResolver
{
    ExecutablePathResolution Resolve(string executablePathWithPlaceholders);
}

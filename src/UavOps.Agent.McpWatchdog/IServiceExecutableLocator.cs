namespace UavOps.Agent.McpWatchdog;

public enum ServiceExecutableLookupKind
{
    /// <summary>Exactly one real service folder is the best match for the hint.</summary>
    Resolved,

    /// <summary>More than one real service folder is comparably close to the hint — the caller
    /// should surface the candidates rather than guess.</summary>
    Ambiguous,

    /// <summary>No real service folder is a plausible match (or the lookup couldn't even be
    /// attempted — e.g. the products placeholder isn't configured).</summary>
    NotFound
}

public sealed record ServiceExecutableLookupResult(
    ServiceExecutableLookupKind Kind, string? ExecutablePath = null, IReadOnlyList<string>? Candidates = null);

/// <summary>Finds a new watchdog service's executable by scanning the real subfolders of
/// <c>%MoavProducts%\Services</c> on disk and matching the operator's (possibly inexact) service
/// name against the actual folder names — a second, disk-based source of truth alongside
/// <c>Contracts.ServiceConfigNaming</c>'s purely pattern-based inference from sibling config
/// entries. Behind an interface for the same unit-testability reason as
/// <see cref="IExecutablePathResolver"/>.</summary>
public interface IServiceExecutableLocator
{
    ServiceExecutableLookupResult Locate(string serviceNameHint);
}

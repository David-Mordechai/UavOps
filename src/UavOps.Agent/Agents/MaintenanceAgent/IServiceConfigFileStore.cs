using UavOps.Agent.Contracts;

namespace UavOps.Agent.Agents.MaintenanceAgent;

/// <summary>Reads and writes a watchdog service-definition YAML config file per named
/// configuration — owns path safety, the leading comment header's preservation across writes, and
/// YAML parsing/emission. Behind an interface so <c>WatchdogConfigService</c> (which owns only
/// business rules — uniqueness, merge-on-update, not-found errors) is unit-testable without real
/// files, mirroring how <c>SimulatorService</c> sits on top of <c>IVmwareController</c>/
/// <c>ILocalLessonRunner</c>.</summary>
public interface IServiceConfigFileStore
{
    Task<IReadOnlyList<string>> ListConfigurationsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ServiceConfigEntry>> ReadAsync(string configurationName, CancellationToken cancellationToken);

    Task WriteAsync(string configurationName, IReadOnlyList<ServiceConfigEntry> entries, CancellationToken cancellationToken);
}

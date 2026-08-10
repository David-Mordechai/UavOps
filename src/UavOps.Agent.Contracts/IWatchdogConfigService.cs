namespace UavOps.Agent.Contracts;

/// <summary>
/// Reads and writes the watchdog's own service-definition config files — one YAML file per named
/// configuration (e.g. "Flight", "Simulator"), each a list of <see cref="ServiceConfigEntry"/>
/// describing what child processes the watchdog should launch and supervise under that
/// configuration. Deliberately a sibling to <c>IWatchdogService</c>, not an extension of it: that
/// interface is about the watchdog's own live health/process (HTTP polling, Service Control
/// Manager start/stop/restart of the watchdog itself); this one is about editing the declarative
/// config files that decide what the watchdog will launch as its *children* — a materially
/// different, more sensitive capability (it configures what code eventually runs), so it gets its
/// own interface/agent/confirmation posture rather than diluting <c>IWatchdogService</c>'s
/// contract. Mirrors <c>ISimulatorService</c>/<c>IWatchdogService</c>'s shape exactly (uniform
/// <see cref="OperationResult"/>, <see cref="CancellationToken"/> last) so it plugs into the same
/// <c>Tooling.OperationCatalog</c>/<c>Tooling.OperationTool</c> reflection machinery — a fourth
/// reflected interface, not a parallel mechanism.
///
/// Implemented by <c>Agents.MaintenanceAgent.WatchdogConfigService</c> (real, reads/writes actual
/// files under <c>WatchdogOptions.ServiceConfigBasePath</c>) and
/// <c>UavOps.Agent.Watchdog.Fake.FakeWatchdogConfigService</c> (in-memory, no real files).
/// </summary>
public interface IWatchdogConfigService
{
    Task<OperationResult> ListConfigurations(CancellationToken cancellationToken);

    Task<OperationResult> ListConfiguredServices(string configurationName, CancellationToken cancellationToken);

    /// <summary><paramref name="executable"/> is nullable: when the operator didn't give a full
    /// path, pass <c>null</c> and the implementation infers one from the naming convention of the
    /// other services already present in that same configuration — never invent one without basis.
    /// Every other optional field also accepts <c>null</c> when not specified. <paramref
    /// name="disabled"/> is the single on/off toggle exposed to chat — an operator asking to
    /// enable/disable a service maps to <c>disabled: false</c>/<c>true</c> respectively; there is
    /// deliberately no separate "enabled" parameter here (see <see cref="ServiceConfigEntry.Enabled"/>)
    /// since having both independently settable proved confusing (the two could end up implying
    /// different things for the same entry).</summary>
    Task<OperationResult> AddConfiguredService(
        string configurationName,
        string description,
        string? executable,
        List<string>? args,
        string? id,
        bool? disabled,
        int? retries,
        bool? isManaged,
        string? healthEndPoint,
        CancellationToken cancellationToken);

    /// <summary><paramref name="description"/> identifies the existing entry to update.
    /// <paramref name="newDescription"/> renames it when given. Every other parameter is nullable
    /// and means "leave unchanged" when <c>null</c> — pass a value only for fields actually being
    /// changed. <paramref name="disabled"/> is the single on/off toggle exposed to chat — see
    /// <see cref="AddConfiguredService"/>.</summary>
    Task<OperationResult> UpdateConfiguredService(
        string configurationName,
        string description,
        string? newDescription,
        string? executable,
        List<string>? args,
        string? id,
        bool? disabled,
        int? retries,
        bool? isManaged,
        string? healthEndPoint,
        CancellationToken cancellationToken);

    Task<OperationResult> RemoveConfiguredService(string configurationName, string description, CancellationToken cancellationToken);
}

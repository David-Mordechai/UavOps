namespace UavOps.Agent.Contracts;

/// <summary>
/// Reports the health of every service a "watchdog" process supervises, and starts/stops/restarts
/// them. Mirrors <see cref="ISimulatorService"/>'s shape exactly (uniform <see cref="OperationResult"/>,
/// <see cref="CancellationToken"/> last) so it plugs into the same <c>Tooling.OperationCatalog</c>/
/// <c>Tooling.OperationTool</c> reflection machinery as the other two domains — a third reflected
/// interface, not a parallel mechanism.
///
/// <see cref="GetServicesHealth"/> deliberately never calls out live — the watchdog's HTTP health
/// endpoint is polled in the background every few seconds and cached, so this just reads that
/// cached snapshot (see <c>Agents.MaintenanceAgent.WatchdogHealthPoller</c>/
/// <c>IWatchdogHealthStore</c>). Start/Stop/Restart are a separate concern: the watchdog's health
/// endpoint is read-only telemetry, not a control plane — those three control the real Windows
/// services directly via the local Service Control Manager (see
/// <c>Agents.MaintenanceAgent.IWindowsServiceController</c>).
///
/// Implemented by <c>Agents.MaintenanceAgent.WatchdogService</c> (real, polls HTTP + controls
/// Windows services) and <c>UavOps.Agent.Watchdog.Fake.FakeWatchdogService</c> (in-memory, nothing
/// installed needed) — this interface lives in the shared contracts project specifically so the
/// Fake implementation can live in its own DLL without a circular reference back to the main app.
/// </summary>
public interface IWatchdogService
{
    Task<OperationResult> GetServicesHealth(CancellationToken cancellationToken);
    Task<OperationResult> StartService(string serviceName, CancellationToken cancellationToken);
    Task<OperationResult> StopService(string serviceName, CancellationToken cancellationToken);
    Task<OperationResult> RestartService(string serviceName, CancellationToken cancellationToken);
}

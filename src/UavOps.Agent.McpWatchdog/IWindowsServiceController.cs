namespace UavOps.Agent.McpWatchdog;

/// <summary>Starts/stops/restarts a local Windows service by its Service Control Manager name.
/// Behind an interface so <see cref="WatchdogService"/> is unit-testable without a real Windows
/// service to control — same reasoning <c>Agents.SimulatorAgent.IVmwareController</c> exists
/// for <c>SimulatorService</c>.</summary>
public interface IWindowsServiceController
{
    Task Start(string serviceName, CancellationToken cancellationToken);
    Task Stop(string serviceName, CancellationToken cancellationToken);
    Task Restart(string serviceName, CancellationToken cancellationToken);
}

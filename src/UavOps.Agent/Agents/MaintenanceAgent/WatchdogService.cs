using UavOps.Agent.Contracts;
using UavOps.Agent.Options;

namespace UavOps.Agent.Agents.MaintenanceAgent;

/// <summary>
/// Implements <see cref="IWatchdogService"/> on top of <see cref="IWatchdogHealthStore"/> (the
/// background poller's cached state — <see cref="GetServicesHealth"/> never calls out live) and
/// <see cref="IWindowsServiceController"/> (the local Service Control Manager). Every method wraps
/// its work in try/catch and returns <see cref="OperationResult.Fail"/>/<c>ErrorMessage</c> on
/// failure — never throws — the same convention <c>Agents.SimulatorAgent.SimulatorService</c>
/// already follows.
/// </summary>
public sealed class WatchdogService(
    IWatchdogHealthStore healthStore,
    IWindowsServiceController serviceController,
    WatchdogOptions options,
    ILogger<WatchdogService> logger) : IWatchdogService
{
    public Task<OperationResult> GetServicesHealth(CancellationToken cancellationToken) =>
        RunAsync("GetServicesHealth", () =>
        {
            var snapshot = healthStore.Current;

            // Nothing polled yet (e.g. app just started) — report an explicit unknown/stale state
            // rather than failing the call; the tool's description already tells the model this
            // is cached data, not a live check.
            if (snapshot is null)
            {
                return Task.FromResult(OperationResult.Ok(new
                {
                    overallStatus = "Unknown",
                    services = new Dictionary<string, ServiceHealthEntry>(),
                    polledAt = (DateTimeOffset?)null,
                    stale = true
                }));
            }

            return Task.FromResult(OperationResult.Ok(new
            {
                overallStatus = snapshot.OverallStatus,
                services = snapshot.Services,
                polledAt = (DateTimeOffset?)snapshot.PolledAt,
                stale = snapshot.Stale
            }));
        });

    public Task<OperationResult> StartService(string serviceName, CancellationToken cancellationToken) =>
        Control(serviceName, "StartService", serviceController.Start, cancellationToken);

    public Task<OperationResult> StopService(string serviceName, CancellationToken cancellationToken) =>
        Control(serviceName, "StopService", serviceController.Stop, cancellationToken);

    public Task<OperationResult> RestartService(string serviceName, CancellationToken cancellationToken) =>
        Control(serviceName, "RestartService", serviceController.Restart, cancellationToken);

    private Task<OperationResult> Control(string serviceName, string operationName,
        Func<string, CancellationToken, Task> action, CancellationToken cancellationToken) =>
        RunAsync(operationName, async () =>
        {
            // The watchdog's health-entry name and the real Windows/SCM service name are not the
            // same string (confirmed) — resolve via the configured map rather than ever guessing.
            if (!options.ServiceNameMap.TryGetValue(serviceName, out var windowsServiceName))
            {
                return OperationResult.Invalid(
                    $"No Windows service is mapped for watchdog service '{serviceName}'. " +
                    "Check the Watchdog:ServiceNameMap configuration.");
            }

            await action(windowsServiceName, cancellationToken);
            return OperationResult.Ok(new { serviceName });
        });

    private async Task<OperationResult> RunAsync(string operationName, Func<Task<OperationResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Watchdog operation {Operation} failed", operationName);
            return OperationResult.Fail(OperationError.ClientReportedError, ex.Message);
        }
    }
}

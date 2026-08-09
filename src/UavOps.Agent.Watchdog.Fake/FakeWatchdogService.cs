using System.Collections.Concurrent;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Watchdog.Fake;

/// <summary>
/// In-memory <see cref="IWatchdogService"/> — no watchdog HTTP endpoint or real Windows services
/// involved. Selected via <c>WatchdogBackend: Fake</c> (the default), the same reasoning
/// <c>UavOps.Agent.Simulator.Fake.FakeSimulatorService</c> already establishes: most dev machines
/// don't have the real dependency running, so local dev/testing of the MaintenanceAgent branch
/// needs a zero-setup path that still exercises the full agent/chat/tool flow end to end.
/// </summary>
public sealed class FakeWatchdogService : IWatchdogService
{
    private static readonly (string Name, string Status, string? Description)[] FakeServices =
    [
        ("telemetry-relay", "Healthy", null),
        ("mission-planner", "Healthy", null),
        ("payload-bridge", "Degraded", "High response latency")
    ];

    // Per-instance (this service is registered as a singleton), not per-call — mirrors a real
    // service staying started/stopped across the rest of a turn (and across turns) once toggled,
    // until the process restarts. Only entries this fake has explicitly toggled are present; a
    // missing entry means "still at its default fake status" (see GetServicesHealth).
    private readonly ConcurrentDictionary<string, bool> _running = new();

    public Task<OperationResult> GetServicesHealth(CancellationToken cancellationToken)
    {
        var services = FakeServices.ToDictionary(
            s => s.Name,
            s => new
            {
                status = _running.TryGetValue(s.Name, out var running) ? (running ? "Healthy" : "Unhealthy") : s.Status,
                description = s.Description
            });

        var overallStatus = services.Values.Any(s => s.status != "Healthy") ? "Degraded" : "Healthy";

        return Task.FromResult(OperationResult.Ok(new
        {
            overallStatus,
            services,
            polledAt = DateTimeOffset.UtcNow,
            stale = false
        }));
    }

    public Task<OperationResult> StartService(string serviceName, CancellationToken cancellationToken) =>
        SetRunning(serviceName, running: true);

    public Task<OperationResult> StopService(string serviceName, CancellationToken cancellationToken) =>
        SetRunning(serviceName, running: false);

    public Task<OperationResult> RestartService(string serviceName, CancellationToken cancellationToken) =>
        SetRunning(serviceName, running: true);

    private Task<OperationResult> SetRunning(string serviceName, bool running)
    {
        if (FakeServices.All(s => !string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(OperationResult.Invalid(
                $"Unknown service '{serviceName}' (fake backend only knows: {string.Join(", ", FakeServices.Select(s => s.Name))})."));
        }

        _running[serviceName] = running;
        return Task.FromResult(OperationResult.Ok(new { serviceName, running }));
    }
}

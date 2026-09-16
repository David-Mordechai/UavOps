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
    // The one name StartService/StopService/RestartService ever call this with - WatchdogTools's
    // own real methods take no model-visible service-name parameter and always hardcode this exact
    // literal (see that class's own WatchdogServiceName constant; duplicated here rather than
    // referenced, since this Fake project deliberately doesn't reference UavOps.Agent.McpWatchdog -
    // see this project's own ServiceCollectionExtensions doc comment for why). Genuinely distinct
    // from FakeServices below: those are the child services the watchdog reports health for, not
    // the watchdog process itself, which is the only thing Start/Stop/Restart ever target - live-
    // reproduced this session: every Start/Stop/RestartService call failed "service not found"
    // under WatchdogBackend: Fake before this existed, because none of FakeServices' names matched
    // what WatchdogTools actually calls with.
    private const string WatchdogServiceName = "Moav.Watchdog.Service";

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
        var isKnown = string.Equals(serviceName, WatchdogServiceName, StringComparison.OrdinalIgnoreCase)
            || FakeServices.Any(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        if (!isKnown)
        {
            return Task.FromResult(OperationResult.Invalid(
                $"Unknown service '{serviceName}' (fake backend only knows: {WatchdogServiceName}, {string.Join(", ", FakeServices.Select(s => s.Name))})."));
        }

        _running[serviceName] = running;
        return Task.FromResult(OperationResult.Ok(new { serviceName, running }));
    }
}

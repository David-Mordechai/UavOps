using System.Collections.Concurrent;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Simulator.Fake;

/// <summary>
/// In-memory <see cref="ISimulatorInfraService"/> — no VMware, VM, or `vmrun` involved. Referenced
/// by <c>UavOps.Agent.McpSimulator</c> under <c>SimulatorBackend: Fake</c> (the default), same
/// zero-setup-dev reasoning as <see cref="FakeSimulatorService"/> (this domain's other half — see
/// <c>ISimulatorInfraService</c>'s own doc comment for why the two are split).
/// </summary>
public sealed class FakeSimulatorInfraService : ISimulatorInfraService
{
    private static readonly string[] FakeNetworkAdapters = ["ethernet0", "ethernet1"];

    // Per-instance (this service is registered as a singleton), not per-call — mirrors a real VM
    // staying "up" across the rest of a turn (and across turns) once started, until the process
    // restarts.
    private readonly ConcurrentDictionary<string, bool> _running = new();

    public Task<OperationResult> EnsureVmwareHostRunning(CancellationToken cancellationToken)
    {
        var (started, alreadyRunning) = TryStart("host");
        return Task.FromResult(OperationResult.Ok(new { started, alreadyRunning, ready = true }));
    }

    public Task<OperationResult> EnsureSimulatorVmRunning(CancellationToken cancellationToken)
    {
        var (started, alreadyRunning) = TryStart("vm");
        return Task.FromResult(OperationResult.Ok(new
        {
            started,
            alreadyRunning,
            networkAdaptersReconnected = FakeNetworkAdapters,
            networkAdaptersFailedToReconnect = Array.Empty<string>(),
            guestReady = true
        }));
    }

    public Task<OperationResult> ListSimulatorLessons(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok(FakeLessonExecutor.FakeLessons.ToList()));

    private (bool Started, bool AlreadyRunning) TryStart(string key)
    {
        var alreadyRunning = !_running.TryAdd(key, true);
        return (Started: !alreadyRunning, AlreadyRunning: alreadyRunning);
    }
}

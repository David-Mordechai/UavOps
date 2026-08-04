using System.Collections.Concurrent;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Simulator.Fake;

/// <summary>
/// In-memory <see cref="ISimulatorService"/> — no VMware, VM, or `vmrun` involved. Selected via
/// <c>SimulatorBackend: Fake</c> (the default), the same reasoning
/// <c>Agents.MoavAgent.Simulation.SimulatedUavOperationService</c> already establishes on the UAV
/// side: most dev machines don't have the real dependency (here, VMware + a lesson-running VM)
/// installed, so local dev/testing of the SimulatorAgent branch needs a zero-setup path that still
/// exercises the full agent/chat/prompt/confirmation flow end to end — only the actual
/// `vmrun`/process I/O is faked, everything above <see cref="ISimulatorService"/> (routing, tool
/// selection, the operator chat prompt, the confirmation gate) runs for real.
/// </summary>
public sealed class FakeSimulatorService(ISimulatorLessonJobQueue lessonJobQueue) : ISimulatorService
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

    public Task<OperationResult> RunSimulatorLesson(string lessonName, CancellationToken cancellationToken)
    {
        // Same thin enqueue-and-return shape as the real backend — FakeLessonExecutor (run by the
        // same backend-agnostic SimulatorLessonJobProcessor) simulates the delay and outcome.
        lessonJobQueue.Enqueue(new SimulatorLessonJob(lessonName, "", DateTimeOffset.UtcNow));
        return Task.FromResult(OperationResult.Ok(new { status = "queued", lessonName }));
    }

    private (bool Started, bool AlreadyRunning) TryStart(string key)
    {
        var alreadyRunning = !_running.TryAdd(key, true);
        return (Started: !alreadyRunning, AlreadyRunning: alreadyRunning);
    }
}

namespace UavOps.Agent.Contracts;

/// <summary>A real queue (not just a "busy" flag) for background lesson runs — a second
/// `RunSimulatorLesson` call while one is already running waits its turn instead of being
/// rejected. See <c>Agents.SimulatorAgent.SimulatorLessonJobProcessor</c> for the consumer
/// side.</summary>
public interface ISimulatorLessonJobQueue
{
    /// <summary>Enqueues a job and returns immediately — never awaits execution.</summary>
    void Enqueue(SimulatorLessonJob job);

    IAsyncEnumerable<SimulatorLessonJob> ReadAllAsync(CancellationToken cancellationToken);
}

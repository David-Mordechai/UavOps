namespace UavOps.Agent.Contracts;

/// <summary>One queued "run this lesson in the background" request — see
/// <see cref="ISimulatorLessonJobQueue"/>/<c>Agents.SimulatorAgent.SimulatorLessonJobProcessor</c>.</summary>
/// <param name="CorrelationId">The originating chat turn's correlationId, for log tracing only —
/// empty when unavailable, since <see cref="ISimulatorService"/> methods (reflected via
/// <c>Tooling.OperationCatalog</c>) don't receive it, only their declared business parameters.
/// The proactive completion notice always gets its own fresh correlationId regardless.</param>
public sealed record SimulatorLessonJob(string LessonName, string CorrelationId, DateTimeOffset EnqueuedAt);

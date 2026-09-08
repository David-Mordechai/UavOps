namespace UavOps.Agent.McpSimulator;

/// <summary>One queued "run this lesson in the background" request — see
/// <see cref="ISimulatorLessonJobQueue"/>/<see cref="SimulatorLessonJobProcessor"/>.</summary>
public sealed record SimulatorLessonJob(string LessonName, DateTimeOffset EnqueuedAt);

using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>Reports a finished background lesson run back to <c>UavOps.Agent</c> so it can produce
/// the "simple terms" proactive chat summary - see <see cref="SimulatorLessonJobProcessor"/> (the
/// "what happened" step, decided here) and <c>UavOps.Agent.Hubs.ChatHub.PushLessonOutcome</c> (the
/// "how to say it" step, decided there - it alone has access to BrainAgent's own persona/model,
/// which is a host-only concept).</summary>
public interface ILessonOutcomeNotifier
{
    Task PushAsync(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration, CancellationToken cancellationToken);
}

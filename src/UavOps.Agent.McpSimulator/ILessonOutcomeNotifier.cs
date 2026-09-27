using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>Tells the operator how a background lesson run ended - see
/// <see cref="SimulatorLessonJobProcessor"/> (what happened) and <see cref="HubLessonOutcomeNotifier"/>
/// (what to tell the operator, decided here too; the host only phrases it in BrainAgent's voice,
/// since its persona and model live there).</summary>
public interface ILessonOutcomeNotifier
{
    Task PushAsync(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration, CancellationToken cancellationToken);
}

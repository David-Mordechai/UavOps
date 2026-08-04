using UavOps.Agent.Contracts;

namespace UavOps.Agent.Simulator.Fake;

/// <summary>
/// Fake <see cref="ILessonExecutor"/> — no real process involved. Simulates a short-but-real delay
/// so the background-job → proactive-notification pipeline is exercisable end to end with nothing
/// installed, same reasoning the rest of the Fake backend exists for.
/// </summary>
public sealed class FakeLessonExecutor : ILessonExecutor
{
    /// <summary>Shared with <see cref="FakeSimulatorService"/>'s <c>ListSimulatorLessons</c>
    /// so the fake backend's "discoverable" lessons and "runnable" lessons stay in sync.</summary>
    public static readonly string[] FakeLessons =
    [
        "intro-flight-basics.ps1",
        "advanced-navigation.ps1",
        "emergency-procedures.ps1"
    ];

    public async Task<(LessonOutcome Outcome, string? Detail)> ExecuteAsync(string lessonName, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        if (!FakeLessons.Contains(lessonName, StringComparer.OrdinalIgnoreCase))
        {
            return (LessonOutcome.Failed, $"Unknown lesson '{lessonName}' (fake backend only knows: {string.Join(", ", FakeLessons)}).");
        }

        return (LessonOutcome.Succeeded, null);
    }
}

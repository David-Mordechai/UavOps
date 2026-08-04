namespace UavOps.Agent.Contracts;

/// <summary>How a background lesson run turned out — see <see cref="ILessonExecutor"/>.</summary>
public enum LessonOutcome
{
    Succeeded,
    SucceededWithWarnings,
    Failed
}

/// <summary>
/// Actually runs one lesson to completion and distills the result — the "what happened" half of
/// the background lesson pipeline (see <c>Agents.SimulatorAgent.SimulatorLessonJobProcessor</c>),
/// kept separate from "how to say it" (the proactive LLM summary). Deliberately returns only a
/// concise <see cref="LessonOutcome"/> plus a short detail string, never the full raw script
/// output — that stays inside the implementation (logged for debugging), since nothing downstream
/// needs to parse a multi-KB dump.
/// </summary>
public interface ILessonExecutor
{
    Task<(LessonOutcome Outcome, string? Detail)> ExecuteAsync(string lessonName, CancellationToken cancellationToken);
}

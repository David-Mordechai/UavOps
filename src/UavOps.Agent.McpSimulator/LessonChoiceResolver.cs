namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Deterministic "did the operator already name one of these lessons" match - moved here from the
/// host's own (now-deleted) <c>Tooling.AskOperatorChoiceTool</c> so this domain's own judgment call
/// (whether to bother asking) lives entirely in this domain, not the host. Only auto-resolves on
/// exactly one match; zero or multiple matches means genuinely ambiguous, the safe default for
/// <see cref="SimulatorTools.AskOperatorWhichLesson"/> to fall through to a real chat prompt for.
/// Relying on the model to notice "the operator already named it" and skip asking proved unreliable
/// in practice (observed directly: it asked anyway) - the same class of problem this codebase
/// already solves elsewhere with fixed vocabularies/logic instead of LLM judgment.
/// </summary>
public static class LessonChoiceResolver
{
    public static string? TryAutoResolve(IReadOnlyList<string> lessons, string operatorMessage)
    {
        var matches = lessons
            .Where(lesson =>
                operatorMessage.Contains(lesson, StringComparison.OrdinalIgnoreCase) ||
                operatorMessage.Contains(Path.GetFileNameWithoutExtension(lesson), StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }
}

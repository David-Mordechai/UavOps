using System.Text.RegularExpressions;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Agents.SimulatorAgent;

/// <summary>
/// Real <see cref="ILessonExecutor"/>: runs the lesson via <see cref="ILocalLessonRunner"/>, logs
/// its full raw output for debugging (never passed further than this class), and distills it into
/// a concise outcome. Moved here from the old synchronous <c>SimulatorService.RunSimulatorLesson</c>
/// — same unhealthy-container detection logic, now evaluated in the background instead of on the
/// model's synchronous tool-call path.
/// </summary>
public sealed class LocalLessonExecutor(ILocalLessonRunner lessonRunner, ILogger<LocalLessonExecutor> logger) : ILessonExecutor
{
    private static readonly Regex ExitedWithCode = new(@"Exited \((\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<(LessonOutcome Outcome, string? Detail)> ExecuteAsync(string lessonName, CancellationToken cancellationToken)
    {
        var (exitCode, output, error) = await lessonRunner.RunLessonAsync(lessonName, cancellationToken);

        logger.LogInformation("Lesson {LessonName} finished (exit {ExitCode}). Full output:\n{Output}", lessonName, exitCode, output);

        if (exitCode != 0)
        {
            return (LessonOutcome.Failed, $"exited {exitCode}: {error}");
        }

        // The script itself exiting 0 doesn't mean everything it started is actually healthy —
        // observed in production: a lesson that starts several docker containers exited 0 while
        // two of them were stuck crash-looping ("Restarting (127)").
        var unhealthy = FindUnhealthyContainerLines(output);
        if (unhealthy.Count > 0)
        {
            return (LessonOutcome.SucceededWithWarnings,
                $"{unhealthy.Count} container status line(s) look unhealthy (restarting/exited/dead): {string.Join(" | ", unhealthy)}");
        }

        return (LessonOutcome.Succeeded, null);
    }

    /// <summary>Scans lesson output line-by-line for common Docker "not actually healthy" status
    /// markers. Deliberately simple substring matching rather than parsing `docker ps`'s column
    /// layout (fragile — column widths vary) — surfacing the raw matching lines is enough context
    /// without needing a real table parser. "Restarting (" always indicates a crash loop (a
    /// container only shows this after exiting abnormally with a restart policy). "Exited (0)" is
    /// excluded on purpose — a clean exit is the normal, intended state for a one-shot/init
    /// container, not a failure.</summary>
    private static List<string> FindUnhealthyContainerLines(string output)
    {
        return output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && IsUnhealthyStatusLine(line))
            .ToList();
    }

    private static bool IsUnhealthyStatusLine(string line)
    {
        if (line.Contains("Restarting (", StringComparison.OrdinalIgnoreCase) || line.Contains("Dead", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var match = ExitedWithCode.Match(line);
        return match.Success && match.Groups[1].Value != "0";
    }
}

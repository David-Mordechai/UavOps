using System.Diagnostics;
using System.Text;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Runs a <c>*.ps1</c> file from <see cref="SimulatorOptions.LessonsFolder"/>, a local folder on
/// this machine — no SSH/network hop, since the lessons live alongside this process.
/// </summary>
public sealed class LocalLessonRunner(SimulatorOptions options) : ILocalLessonRunner
{
    public async Task<(int ExitCode, string Output, string Error)> RunLessonAsync(string lessonFileName, CancellationToken cancellationToken)
    {
        var fullPath = ResolveLessonPath(lessonFileName);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Without this, .NET decodes the captured bytes using the system console
                // codepage instead of UTF-8 — observed in production as corrupted characters
                // (e.g. docker ps's truncation ellipsis) in lesson output shown to the operator.
                // Also force the child process itself to emit UTF-8 (its default console output
                // encoding on Windows is the legacy codepage, not UTF-8) via -OutputFormat isn't
                // enough on its own — PowerShell honors [Console]::OutputEncoding, set in the
                // wrapped command below, before invoking the actual lesson script.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        // ArgumentList (not a single Arguments string) so the lesson path is passed as a raw
        // argument, never shell-interpreted.
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            $"& '{fullPath.Replace("'", "''")}'; " +
            // Invoking via `& 'path'` inside -Command does NOT automatically propagate the
            // script's `exit N` as this process's own exit code the way `-File` would (verified
            // empirically: without this, a script calling `exit 7` surfaced here as exit 1
            // instead) — forward $LASTEXITCODE explicitly so ExecuteLesson's success/failure
            // check reflects what the lesson script actually reported.
            "exit $LASTEXITCODE");

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Resolves a lesson name against <see cref="SimulatorOptions.LessonsFolder"/> and
    /// rejects anything that would resolve outside it. <paramref name="lessonFileName"/>
    /// ultimately originates from the operator's chat reply (via
    /// <c>UavOps.Agent.Agents.SimulatorAgent.AskOperatorChoiceTool</c>) — never trusted as a bare
    /// filename, since a reply like <c>"..\..\..\Windows\System32\evil.ps1"</c> must not be able
    /// to escape the lessons folder.</summary>
    private string ResolveLessonPath(string lessonFileName)
    {
        var lessonsFolder = Path.GetFullPath(options.LessonsFolder);
        var fullPath = Path.GetFullPath(Path.Combine(lessonsFolder, lessonFileName));

        if (!fullPath.StartsWith(lessonsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Lesson '{lessonFileName}' resolves outside the configured lessons folder.");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Lesson file not found: '{lessonFileName}'.");
        }

        return fullPath;
    }
}

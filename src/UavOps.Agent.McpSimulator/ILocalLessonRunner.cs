namespace UavOps.Agent.McpSimulator;

/// <summary>Runs a PowerShell lesson script from a local folder on this machine. Behind an
/// interface so <see cref="LocalLessonExecutor"/> is unit-testable without shelling out to a real
/// <c>powershell.exe</c>.</summary>
public interface ILocalLessonRunner
{
    Task<(int ExitCode, string Output, string Error)> RunLessonAsync(string lessonFileName, CancellationToken cancellationToken);
}

namespace UavOps.Agent.Agents.SimulatorAgent;

/// <summary>Discovers and runs PowerShell lesson scripts from a local folder on this machine (the
/// same machine the agent runs on — no remote/SSH hop). Behind an interface so
/// <see cref="SimulatorService"/> is unit-testable without shelling out to a real
/// <c>powershell.exe</c>.</summary>
public interface ILocalLessonRunner
{
    IReadOnlyList<string> ListLessons();
    Task<(int ExitCode, string Output, string Error)> RunLessonAsync(string lessonFileName, CancellationToken cancellationToken);
}

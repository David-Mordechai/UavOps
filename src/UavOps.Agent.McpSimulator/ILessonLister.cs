namespace UavOps.Agent.McpSimulator;

/// <summary>Discovers available PowerShell lesson scripts from a local folder on this machine.
/// Behind an interface so <see cref="SimulatorInfraService"/> is unit-testable without touching
/// the real filesystem. Split out of the old, single <c>ILocalLessonRunner</c> (which also ran a
/// lesson) when lesson listing moved to this process and lesson running stayed host-side in
/// <c>UavOps.Agent</c> — the two capabilities share nothing (listing needs no path-safety check at
/// all, since it takes no operator-supplied input), so splitting them was a clean cut, not a
/// premature abstraction.</summary>
public interface ILessonLister
{
    IReadOnlyList<string> ListLessons();
}

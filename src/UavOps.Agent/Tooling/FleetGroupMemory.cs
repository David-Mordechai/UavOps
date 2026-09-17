namespace UavOps.Agent.Tooling;

/// <summary>
/// Remembers the most recent group of real tail numbers resolved together as a single deliberate
/// multi-target action - an "ALL" fan-out, an explicit comma-separated subset, or several distinct
/// real per-UAV calls to the same tool in one turn (see
/// <see cref="TailNumberResolutionScope.RegisterRealGuessAndGetSiblingsAsync"/>) - so a LATER
/// action that only names PART of that same group can be recognized and completed for the rest.
/// See <see cref="TailNumberDisambiguationTool"/>'s own doc comment for the real, live-reproduced
/// bug this closes: a small local model reliably calling a tool for N-1 of N previously-addressed
/// UAVs and simply never generating the Nth call, then honestly reporting the resulting partial
/// state rather than fabricating - never a wrong answer, just an incomplete one, and no amount of
/// prompt wording (tried repeatedly - see McpMoav/ToolsConfig.yaml's own "Tail numbers" section)
/// closed the gap reliably.
///
/// Deliberately session-scoped (one instance per <see cref="AgentFactory"/> singleton, i.e. the
/// app's whole running lifetime), NOT per-turn like <see cref="TailNumberResolutionScope"/> - the
/// whole point is surviving across the turn boundary a completely fresh per-turn scope would lose.
/// A single mutable slot, not a history: only the MOST RECENT group matters, since an operator
/// explicitly narrowing scope (e.g. "the other two" excluding one UAV) should make that narrower
/// set the new "them" going forward, not silently re-include what was just excluded.
/// </summary>
public sealed class FleetGroupMemory
{
    private readonly object _lock = new();
    private HashSet<string>? _lastFullGroup;

    /// <summary>Only remembered when more than one tail number is involved - a single-UAV
    /// resolution leaves nothing for a later partial reference to complete against.</summary>
    public void RecordFullGroup(IReadOnlyCollection<string> tailNumbers)
    {
        if (tailNumbers.Count <= 1)
        {
            return;
        }

        lock (_lock)
        {
            _lastFullGroup = new HashSet<string>(tailNumbers, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlySet<string>? GetLastFullGroup()
    {
        lock (_lock)
        {
            return _lastFullGroup;
        }
    }
}

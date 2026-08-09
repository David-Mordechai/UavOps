namespace UavOps.Agent.Agents.MaintenanceAgent;

/// <summary>
/// A single <see cref="WatchdogHealthSnapshot"/> reference, swapped atomically on every poll.
/// A reference-type field write is already atomic in .NET, and <see cref="Update"/> never reads
/// before writing, so a plain <c>volatile</c> field (no lock) is sufficient — readers always see
/// either the previous snapshot or the new one in full, never a mix of both.
/// </summary>
public sealed class WatchdogHealthStore : IWatchdogHealthStore
{
    private volatile WatchdogHealthSnapshot? _current;

    public WatchdogHealthSnapshot? Current => _current;

    public void Update(WatchdogHealthSnapshot snapshot) => _current = snapshot;
}

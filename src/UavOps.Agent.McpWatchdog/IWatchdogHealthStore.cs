namespace UavOps.Agent.McpWatchdog;

/// <summary>The cached "latest known" watchdog health state — written only by
/// <see cref="WatchdogHealthPoller"/>, read only by <see cref="WatchdogService"/>. Behind an
/// interface so <see cref="WatchdogService"/> is unit-testable without a real poller running.</summary>
public interface IWatchdogHealthStore
{
    /// <summary>The most recent poll result, or <c>null</c> if the poller hasn't completed a
    /// single successful poll yet.</summary>
    WatchdogHealthSnapshot? Current { get; }

    void Update(WatchdogHealthSnapshot snapshot);
}

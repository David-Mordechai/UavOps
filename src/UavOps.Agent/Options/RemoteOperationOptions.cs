namespace UavOps.Agent.Options;

/// <summary>
/// Which backend answers <c>IOperationService</c> calls: the in-memory simulation, or the
/// SignalR bridge to a real (or mock) fleet-commanding app. Chosen once at startup — see
/// <c>Program.cs</c>.
/// </summary>
public enum OperationBackend
{
    Simulated,
    SignalR
}

public sealed class RemoteOperationOptions
{
    public const string SectionName = "RemoteOperation";

    /// <summary>How long to wait for the connected fleet-commanding client to answer an operation
    /// before giving up. Machine-to-machine SLA, not an operator-facing toggle.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

namespace UavOps.ControlApi.Options;

/// <summary>
/// Which backend answers <c>IUavFleetService</c>/<c>IGdtService</c> calls: the in-memory
/// simulation, or the SignalR bridge to a real (or mock) fleet-commanding app. Chosen once at
/// startup — see <c>Program.cs</c>.
/// </summary>
public enum FleetBackend
{
    Simulated,
    SignalR
}

public sealed class FleetBridgeOptions
{
    public const string SectionName = "FleetBridge";

    /// <summary>How long to wait for the connected fleet-commanding client to answer a command
    /// before giving up. Machine-to-machine SLA, not an operator-facing toggle.</summary>
    public int CommandTimeoutSeconds { get; init; } = 10;
}

namespace UavOps.Agent.Contracts;

/// <summary>
/// The training simulator's VM-readiness and lesson-discovery operations — the part of the
/// simulator domain that moved to its own process (<c>UavOps.Agent.McpSimulator</c>) in the
/// "split BrainAgent's 3 domains into separate MCP servers" redesign. Split out of the old, single
/// <see cref="ISimulatorService"/> specifically because <c>RunSimulatorLesson</c> (still on
/// <see cref="ISimulatorService"/>) stays host-side, wired into the in-process background job
/// queue/proactive-notification pipeline — these three don't need any of that, they're plain
/// synchronous checks/listings.
///
/// Mirrors <c>IOperationService</c>'s shape (uniform <see cref="OperationResult"/>,
/// <see cref="CancellationToken"/> last) so it plugs into the same
/// <c>Tooling.OperationCatalog</c>-free MCP tool-authoring pattern the fleet/watchdog domains
/// already use (a <c>[McpServerToolType]</c> class per domain, not this reflection machinery —
/// this interface exists purely so both the Real and Fake implementations share one contract).
///
/// Implemented by <c>UavOps.Agent.McpSimulator.SimulatorInfraService</c> (real, talks to VMware)
/// and <c>UavOps.Agent.Simulator.Fake.FakeSimulatorInfraService</c> (in-memory, no VMware needed).
/// </summary>
public interface ISimulatorInfraService
{
    Task<OperationResult> EnsureVmwareHostRunning(CancellationToken cancellationToken);
    Task<OperationResult> EnsureSimulatorVmRunning(CancellationToken cancellationToken);
    Task<OperationResult> ListSimulatorLessons(CancellationToken cancellationToken);
}

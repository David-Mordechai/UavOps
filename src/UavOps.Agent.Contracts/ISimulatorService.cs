namespace UavOps.Agent.Contracts;

/// <summary>
/// Sets up and drives the training-simulator environment: the local VMware Workstation host/guest
/// VM, and lesson script discovery from a local folder on this same machine (no SSH/network hop —
/// the lessons live alongside the agent, not on the VM). Mirrors <c>IOperationService</c>'s shape
/// exactly (uniform <see cref="OperationResult"/>, <see cref="CancellationToken"/> last) so it
/// plugs into the same <c>Tooling.OperationCatalog</c>/<c>Tooling.OperationTool</c> reflection
/// machinery as the UAV operations — a second reflected interface for a second domain, not a
/// parallel mechanism.
///
/// Implemented by <c>Agents.SimulatorAgent.SimulatorService</c> (real, talks to VMware) and
/// <c>UavOps.Agent.Simulator.Fake.FakeSimulatorService</c> (in-memory, no VMware needed) — this
/// interface lives in the shared contracts project specifically so the Fake implementation can
/// live in its own DLL without a circular reference back to the main app.
///
/// The fifth requested tool ("ask the operator which lesson to run") is deliberately not here —
/// its job is to prompt-and-wait in chat, not to be a data operation, so it's a hand-built
/// <c>AIFunction</c> instead (see <c>AgentToolConfig.Kind</c>).
/// </summary>
public interface ISimulatorService
{
    Task<OperationResult> EnsureVmwareHostRunning(CancellationToken cancellationToken);
    Task<OperationResult> EnsureSimulatorVmRunning(CancellationToken cancellationToken);
    Task<OperationResult> ListSimulatorLessons(CancellationToken cancellationToken);
    Task<OperationResult> RunSimulatorLesson(string lessonName, CancellationToken cancellationToken);
}

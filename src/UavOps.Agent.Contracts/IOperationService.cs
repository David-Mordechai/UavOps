namespace UavOps.Agent.Contracts;

/// <summary>
/// Commands and queries a fleet of UAVs and their ground data terminals, each addressed by tail
/// number. <c>UavOps.Agent.McpMoav</c>'s in-memory implementation and (dormant, deferred)
/// <c>UavOps.Agent</c>'s <c>RemoteOperationService</c> (relayed to a real/mock fleet-commanding
/// app) both implement this — callers only depend on this interface, never on how operations are
/// actually carried out. Lives in <c>Contracts</c> (not under <c>UavOps.Agent</c>'s own
/// <c>Agents/MoavAgent/</c>, where it lived before the MCP domain split) specifically so both the
/// host process (for the dormant SignalR path, and for <c>TailNumberDisambiguationTool</c>'s
/// <see cref="OperationResult"/> return shape) and the separate <c>UavOps.Agent.McpMoav</c>
/// process can reference it without one needing to reference the other's project.
///
/// This interface (and the hub/broker/tool machinery around it) is intentionally domain-agnostic
/// in naming — <c>UavOps.Agent</c> is a general agentic tool-calling framework that currently has
/// a UAV domain plugged into it via these 12 method signatures, not a UAV-specific system. The
/// method names/parameters themselves stay UAV-flavored on purpose: they're domain data, not
/// infrastructure, and <c>Hubs.IOperationClientProxy</c>'s matching method names are additionally
/// pinned by the (unchanged) net47 client's wire contract.
/// </summary>
public interface IOperationService
{
    Task<OperationResult> ListFleet(CancellationToken cancellationToken);
    Task<OperationResult> GetTelemetry(string tailNumber, CancellationToken cancellationToken);
    Task<OperationResult> Navigate(string tailNumber, string location, CancellationToken cancellationToken);
    Task<OperationResult> SetSpeed(string tailNumber, int speedKts, CancellationToken cancellationToken);
    Task<OperationResult> SetAltitude(string tailNumber, int altitudeFt, CancellationToken cancellationToken);
    Task<OperationResult> ReturnToLaunch(string tailNumber, CancellationToken cancellationToken);
    Task<OperationResult> PointPayload(string tailNumber, string location, CancellationToken cancellationToken);
    Task<OperationResult> ResetPayload(string tailNumber, CancellationToken cancellationToken);
    Task<OperationResult> UploadWaypoints(string tailNumber, List<Waypoint> waypoints, CancellationToken cancellationToken);
    Task<OperationResult> GetMissionStatus(string tailNumber, CancellationToken cancellationToken);
    Task<OperationResult> GetLinkStatus(string tailNumber, CancellationToken cancellationToken);
    Task<OperationResult> SetTrackingMode(string tailNumber, string mode, CancellationToken cancellationToken);
}

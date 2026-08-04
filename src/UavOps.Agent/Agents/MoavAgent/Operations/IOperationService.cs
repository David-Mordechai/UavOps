using UavOps.Agent.Contracts;

namespace UavOps.Agent.Agents.MoavAgent.Operations;

/// <summary>
/// Commands and queries a fleet of UAVs and their ground data terminals, each addressed by tail
/// number. This is the seam <c>Simulation/SimulatedUavOperationService</c> (in-memory) and
/// <c>Operations/Remote/RemoteOperationService</c> (relayed to a real/mock fleet-commanding app)
/// both implement — callers only depend on this interface, never on how operations are actually
/// carried out. It is also the single source of truth for the mechanical tool contract:
/// <c>Tooling/OperationCatalog.cs</c> builds its descriptor table by reflecting over this
/// interface's methods, so adding a 13th operation means adding one method here — nothing else
/// to hand-sync.
///
/// This interface (and the hub/broker/catalog/tool machinery around it) is intentionally
/// domain-agnostic in naming — <c>UavOps.Agent</c> is a general agentic tool-calling framework
/// that currently has a UAV domain plugged into it via these 12 method signatures, not a
/// UAV-specific system. The method names/parameters themselves stay UAV-flavored on purpose:
/// they're domain data, not infrastructure, and <c>Hubs.IOperationClientProxy</c>'s matching
/// method names are additionally pinned by the (unchanged) net47 client's wire contract. Lives
/// under <c>Agents/MoavAgent/</c> since it (and everything that implements/relays it) is only
/// ever reachable through MoavAgent's subtree.
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

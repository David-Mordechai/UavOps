namespace UavOps.Agent.McpMoav;

/// <summary>
/// Which <c>IOperationService</c> implementation this server's <see cref="MoavTools"/> use: the
/// in-memory simulation, or the SignalR relay to the real (or mock) Moav-commanding app connected
/// to <c>UavOps.Agent</c>'s own hub (see <see cref="MoavRelayService"/>). Chosen once at startup
/// via the top-level <c>OperationBackend</c> config key - same name/values
/// (<c>Simulated</c>/<c>SignalR</c>) as this project's own predecessor in <c>UavOps.Agent</c> had,
/// preserved exactly so an existing deployment's config doesn't need its enum value renamed, only
/// relocated to this project's own <c>appsettings.json</c>.
/// </summary>
public enum OperationBackend
{
    Simulated,
    SignalR
}

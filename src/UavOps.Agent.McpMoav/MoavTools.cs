using System.Text.Json;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpMoav;

/// <summary>
/// The live Moav (UAV fleet) domain's MCP tools - one method per <see cref="IOperationService"/>
/// operation, wrapping whichever <see cref="IOperationService"/> implementation <c>Program.cs</c>
/// registered for the configured <see cref="OperationBackend"/>
/// (<see cref="SimulatedUavOperationService"/>, or <see cref="MoavRelayService"/> for the real
/// Moav-hardware path) exactly the way the host process's own (now-removed)
/// <c>Tooling/OperationTool.cs</c> used to: camelCase JSON on success, a plain <c>"Error: ..."</c>
/// string on failure (see <see cref="ToResultText"/>) - so which backend is active changes nothing
/// about the result shape the model sees. No <c>[McpServerTool]</c>/<c>[Description]</c> attributes
/// here anymore - every method's name/description/parameter descriptions/annotations come from this
/// project's own <c>ToolsConfig.yaml</c>, built at startup by <see cref="McpToolsBuilder"/>
/// (<c>Program.cs</c>) - editing a description or adding a parameter description needs only a YAML
/// edit and a process restart, never a rebuild. Method names still have to match that YAML's
/// <c>operation:</c> entries exactly (case-sensitive) - <see cref="McpToolsBuilder.Build"/> fails
/// fast at startup if either side has an entry the other doesn't.
/// </summary>
public static class MoavTools
{
    private static readonly JsonSerializerOptions ResultSerializeOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string ToResultText(OperationResult result) =>
        result.Success ? JsonSerializer.Serialize(result.Value, ResultSerializeOptions) : $"Error: {result.ErrorMessage}";

    public static async Task<string> ListFleet(IOperationService moav, CancellationToken cancellationToken) =>
        ToResultText(await moav.ListFleet(cancellationToken));

    public static async Task<string> GetTelemetry(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.GetTelemetry(tailNumber, cancellationToken));

    public static async Task<string> Navigate(IOperationService moav, string tailNumber, string location, CancellationToken cancellationToken) =>
        ToResultText(await moav.Navigate(tailNumber, location, cancellationToken));

    public static async Task<string> SetSpeed(IOperationService moav, string tailNumber, int speedKts, CancellationToken cancellationToken) =>
        ToResultText(await moav.SetSpeed(tailNumber, speedKts, cancellationToken));

    public static async Task<string> SetAltitude(IOperationService moav, string tailNumber, int altitudeFt, CancellationToken cancellationToken) =>
        ToResultText(await moav.SetAltitude(tailNumber, altitudeFt, cancellationToken));

    public static async Task<string> ReturnToLaunch(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.ReturnToLaunch(tailNumber, cancellationToken));

    public static async Task<string> PointPayload(IOperationService moav, string tailNumber, string location, CancellationToken cancellationToken) =>
        ToResultText(await moav.PointPayload(tailNumber, location, cancellationToken));

    public static async Task<string> ResetPayload(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.ResetPayload(tailNumber, cancellationToken));

    public static async Task<string> GetLinkStatus(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.GetLinkStatus(tailNumber, cancellationToken));

    public static async Task<string> SetTrackingMode(IOperationService moav, string tailNumber, string mode, CancellationToken cancellationToken) =>
        ToResultText(await moav.SetTrackingMode(tailNumber, mode, cancellationToken));

    public static async Task<string> UploadWaypoints(IOperationService moav, string tailNumber, List<Waypoint> waypoints, CancellationToken cancellationToken) =>
        ToResultText(await moav.UploadWaypoints(tailNumber, waypoints, cancellationToken));

    public static async Task<string> GetMissionStatus(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.GetMissionStatus(tailNumber, cancellationToken));
}

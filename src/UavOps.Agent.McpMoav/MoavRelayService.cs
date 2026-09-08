using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpMoav;

/// <summary>
/// <see cref="IOperationService"/> backed by a SignalR client connection to <c>UavOps.Agent</c>'s
/// own hub (<c>ChatHub</c>'s <c>Relay*</c> methods there) instead of in-memory state - the real
/// Moav-hardware path, selected via <see cref="OperationBackend.SignalR"/>. Each method here is
/// the exact same one-liner the old, now-deleted, in-host <c>RemoteOperationService</c> had; only
/// where it runs changed (this separate process, reached over its own SignalR client connection,
/// rather than in-process against a directly-injected broker).
///
/// The host's <c>Relay*</c> hub methods already return the final camelCase-JSON-or-"Error: ..."
/// text (see <c>ChatHub</c>'s own doc comment for why a plain string, not <see cref="OperationResult"/>
/// itself, crosses this particular wire) - so every method here just parses that text back into an
/// <see cref="OperationResult"/>, the shape <see cref="MoavTools"/> already expects from either
/// backend.
/// </summary>
public sealed class MoavRelayService(HubConnection hubConnection) : IOperationService
{
    public Task<OperationResult> ListFleet(CancellationToken cancellationToken) =>
        RelayAsync("RelayListFleet", cancellationToken);

    public Task<OperationResult> GetTelemetry(string tailNumber, CancellationToken cancellationToken) =>
        RelayAsync("RelayGetTelemetry", cancellationToken, tailNumber);

    public Task<OperationResult> Navigate(string tailNumber, string location, CancellationToken cancellationToken) =>
        RelayAsync("RelayNavigate", cancellationToken, tailNumber, location);

    public Task<OperationResult> SetSpeed(string tailNumber, int speedKts, CancellationToken cancellationToken) =>
        RelayAsync("RelaySetSpeed", cancellationToken, tailNumber, speedKts);

    public Task<OperationResult> SetAltitude(string tailNumber, int altitudeFt, CancellationToken cancellationToken) =>
        RelayAsync("RelaySetAltitude", cancellationToken, tailNumber, altitudeFt);

    public Task<OperationResult> ReturnToLaunch(string tailNumber, CancellationToken cancellationToken) =>
        RelayAsync("RelayReturnToLaunch", cancellationToken, tailNumber);

    public Task<OperationResult> PointPayload(string tailNumber, string location, CancellationToken cancellationToken) =>
        RelayAsync("RelayPointPayload", cancellationToken, tailNumber, location);

    public Task<OperationResult> ResetPayload(string tailNumber, CancellationToken cancellationToken) =>
        RelayAsync("RelayResetPayload", cancellationToken, tailNumber);

    public Task<OperationResult> UploadWaypoints(string tailNumber, List<Waypoint> waypoints, CancellationToken cancellationToken) =>
        RelayAsync("RelayUploadWaypoints", cancellationToken, tailNumber, waypoints);

    public Task<OperationResult> GetMissionStatus(string tailNumber, CancellationToken cancellationToken) =>
        RelayAsync("RelayGetMissionStatus", cancellationToken, tailNumber);

    public Task<OperationResult> GetLinkStatus(string tailNumber, CancellationToken cancellationToken) =>
        RelayAsync("RelayGetLinkStatus", cancellationToken, tailNumber);

    public Task<OperationResult> SetTrackingMode(string tailNumber, string mode, CancellationToken cancellationToken) =>
        RelayAsync("RelaySetTrackingMode", cancellationToken, tailNumber, mode);

    private async Task<OperationResult> RelayAsync(string hubMethod, CancellationToken cancellationToken, params object?[] args)
    {
        string resultText;
        try
        {
            resultText = await InvokeAsync(hubMethod, args, cancellationToken);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(OperationError.NoClientConnected, $"Could not reach UavOps.Agent's Moav hub: {ex.Message}");
        }

        if (resultText.StartsWith("Error: ", StringComparison.Ordinal))
        {
            return OperationResult.Fail(OperationError.ClientReportedError, resultText["Error: ".Length..]);
        }

        using var doc = JsonDocument.Parse(resultText);
        return OperationResult.Ok(doc.RootElement.Clone());
    }

    // HubConnectionExtensions.InvokeAsync<T> only offers fixed-arity overloads (0-10 args) - every
    // Moav operation here has at most 2, so a tiny manual dispatch is simpler than pulling in
    // reflection just to forward an arbitrary-length array.
    private Task<string> InvokeAsync(string hubMethod, object?[] args, CancellationToken cancellationToken) => args.Length switch
    {
        0 => hubConnection.InvokeAsync<string>(hubMethod, cancellationToken),
        1 => hubConnection.InvokeAsync<string>(hubMethod, args[0], cancellationToken),
        2 => hubConnection.InvokeAsync<string>(hubMethod, args[0], args[1], cancellationToken),
        _ => throw new NotSupportedException($"Relay call '{hubMethod}' has {args.Length} arguments - only 0-2 are wired up.")
    };
}

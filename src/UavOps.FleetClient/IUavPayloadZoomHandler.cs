namespace UavOps.FleetClient
{
    /// <summary>
    /// Optional: implement this alongside <see cref="IUavCommandHandler"/> (on the same handler
    /// object) to let the operator zoom the payload camera. Kept separate, like
    /// <see cref="IUavMissionHandler"/>, so an existing app keeps compiling; for one that doesn't
    /// implement it <see cref="FleetClientConnection"/> answers with a failure itself.
    /// </summary>
    public interface IUavPayloadZoomHandler
    {
        /// <summary>Set the payload camera's zoom (1 = widest). Clamp to the payload's own range
        /// and report the zoom and field of view actually set in the returned telemetry
        /// (<see cref="TelemetrySnapshot.PayloadZoom"/>, <see cref="TelemetrySnapshot.PayloadHfovDeg"/>).</summary>
        CommandResult<TelemetrySnapshot> SetPayloadZoom(string tailNumber, double zoom);
    }
}

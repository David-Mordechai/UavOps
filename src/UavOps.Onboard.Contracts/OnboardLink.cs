namespace UavOps.Onboard.Contracts;

/// <summary>
/// The link between the aircraft and its onboard computer, carried over SignalR through the ground
/// agent (UavOps.Agent's <c>/onboardHub</c>): both connect out to the host, which only routes each
/// message by tail number and never reads it (the host holds no domain logic). The onboard computer
/// therefore needs no open port - a UAV's onboard computer dials home, it isn't dialled.
///
/// Commands, reports and status go this way; the camera video and close-ups don't (the onboard
/// computer reads those straight from the aircraft's camera, <see cref="SearchTask.VideoSourceUrl"/>).
/// Every message is (tail, kind, JSON payload); the kinds are <see cref="Kinds"/>.
/// </summary>
public static class OnboardLink
{
    public const string HubPath = "/onboardHub";

    /// <summary>Query parameters on the hub URL: who connects (<see cref="Aircraft"/> or
    /// <see cref="Onboard"/>), and for an onboard computer which tails it serves (comma-separated,
    /// or <c>*</c> for all - one Jetson serves the whole simulated fleet in dev).</summary>
    public const string RoleQuery = "role";
    public const string TailsQuery = "tails";
    public const string Aircraft = "aircraft";
    public const string Onboard = "onboard";

    /// <summary>Hub methods a client calls: send to the other side.</summary>
    public const string ToOnboard = "ToOnboard";
    public const string ToAircraft = "ToAircraft";

    /// <summary>What the hub calls on a client: (tail, kind, payload).</summary>
    public const string Receive = "Receive";

    public static class Kinds
    {
        // Aircraft -> onboard
        /// <summary>A <see cref="Contracts.SearchTask"/>: start (or replace) this UAV's search.</summary>
        public const string TaskStart = "task.start";
        /// <summary>No payload: stop this UAV's search.</summary>
        public const string TaskStop = "task.stop";

        // Onboard -> aircraft
        /// <summary>A list of <see cref="SearchTaskStatus"/>, every couple of seconds (tail "*").</summary>
        public const string Status = "status";
        /// <summary>An <see cref="OnboardDetection"/>.</summary>
        public const string Detection = "detection";
        /// <summary>A <see cref="TargetTrackReport"/>.</summary>
        public const string Track = "track";
        /// <summary>A <see cref="PointAtCommand"/>.</summary>
        public const string PayloadPoint = "payload.point";
        /// <summary>A <see cref="ZoomCommand"/>.</summary>
        public const string PayloadZoom = "payload.zoom";
        /// <summary>No payload.</summary>
        public const string PayloadRelease = "payload.release";
    }
}

namespace UavOps.FleetClient
{
    /// <summary>
    /// Optional: implement this alongside <see cref="IUavCommandHandler"/> (on the same handler
    /// object) to support AOI search missions. Kept separate so an app that only implements
    /// <see cref="IUavCommandHandler"/> keeps compiling unchanged; for such an app
    /// <see cref="FleetClientConnection"/> answers these commands with a failure itself.
    ///
    /// A search mission's route arrives first through
    /// <see cref="IUavCommandHandler.UploadWaypoints"/>; uploading must not start flight.
    /// </summary>
    public interface IUavMissionHandler
    {
        /// <summary>Start flying the uploaded route. The mode should become <c>"Searching"</c>.</summary>
        CommandResult<MissionStatus> StartMission(string tailNumber);

        /// <summary>End the search or tracking mission: stop where it is and circle, payload back to
        /// straight down. Report the mission <c>Aborted</c>.</summary>
        CommandResult<MissionStatus> StopMission(string tailNumber);

        /// <summary>Hand the onboard agent what to look for. When it spots it, report back with
        /// <see cref="FleetClientConnection.ReportDetectionAsync"/>.</summary>
        CommandResult<MissionStatus> SetSearchTarget(string tailNumber, SearchTargetRequest request);
    }
}

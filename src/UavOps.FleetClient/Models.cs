using System;
using System.Collections.Generic;

namespace UavOps.FleetClient
{
    // Net47-side copies of UavOps.Agent.Contracts's OperationModels — no common TFM worth
    // introducing for a handful of tiny records. Property names must match Contracts' (JSON is
    // camelCase on the wire); FleetContractDriftTests in UavOps.Agent.Tests checks they do.
    // FleetClientConnection is the only place that serializes/deserializes these.

    public sealed class Waypoint
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
        public int AltitudeFt { get; set; }
    }

    public sealed class TelemetrySnapshot
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
        public int SpeedKts { get; set; }
        public int AltitudeFt { get; set; }
        public string Mode { get; set; }
        public string PayloadLockedOn { get; set; }

        /// <summary>The payload camera's zoom (1 = widest), or 0 if not reported.</summary>
        public double PayloadZoom { get; set; }

        /// <summary>The payload camera's horizontal field of view at that zoom, in degrees, or 0
        /// if not reported. Search routes are planned from it.</summary>
        public double PayloadHfovDeg { get; set; }
    }

    public sealed class GdtLinkStatus
    {
        public string LinkState { get; set; }
        public int SignalStrengthPercent { get; set; }
        public string TrackingMode { get; set; }
    }

    public sealed class UavSummary
    {
        public string TailNumber { get; set; }
        public string Mode { get; set; }
        public double Lat { get; set; }
        public double Lng { get; set; }
    }

    public sealed class MissionStatus
    {
        public string Mode { get; set; }
        public int WaypointCount { get; set; }

        // AOI search missions only; leave null otherwise.
        public int? CurrentWaypointIndex { get; set; }
        public string ActiveMissionId { get; set; }
        public string SearchPrompt { get; set; }
    }

    /// <summary>What the onboard agent should look for while flying a search mission.</summary>
    public sealed class SearchTargetRequest
    {
        public string MissionId { get; set; }
        public string ZoneName { get; set; }
        public string Prompt { get; set; }
        public double MinConfidence { get; set; }

        /// <summary>Find and track: once found, the onboard agent locks on the target and keeps
        /// reporting where it is (detection updates under its track id, and the
        /// <see cref="MissionEventKinds.Tracking"/>/<see cref="MissionEventKinds.TargetLost"/>/
        /// <see cref="MissionEventKinds.TargetRegained"/> events), and the UAV circles it.</summary>
        public bool Track { get; set; }

        /// <summary>Fly the route again and again (a moving target may not be there on one pass),
        /// reporting <see cref="MissionEventKinds.PassCompleted"/> after each, until
        /// <see cref="IUavMissionHandler.StopMission"/> or a redirect.</summary>
        public bool Repeat { get; set; }
    }

    /// <summary>Sent to the host with <see cref="FleetClientConnection.ReportDetectionAsync"/>
    /// when the onboard agent spots a search target.</summary>
    public sealed class DetectionReport
    {
        public string TailNumber { get; set; }
        public string MissionId { get; set; }
        public string ZoneName { get; set; }
        public string Prompt { get; set; }
        public string Label { get; set; }
        public double Confidence { get; set; }
        public double Lat { get; set; }
        public double Lng { get; set; }
        public DateTime DetectedAtUtc { get; set; }
        public string TrackId { get; set; }
    }

    /// <summary>Sent to the host with <see cref="FleetClientConnection.ReportMissionEventAsync"/>
    /// when a search mission ends.</summary>
    public sealed class MissionEventReport
    {
        public string TailNumber { get; set; }
        public string MissionId { get; set; }
        public string ZoneName { get; set; }

        /// <summary>One of <see cref="MissionEventKinds"/>.</summary>
        public string Kind { get; set; }
    }

    public static class MissionEventKinds
    {
        /// <summary>The route was flown to its end.</summary>
        public const string Completed = "Completed";

        /// <summary>The mission was cut short, e.g. by a Navigate or ReturnToLaunch.</summary>
        public const string Aborted = "Aborted";

        /// <summary>Find and track: the target is found and locked on; the UAV now follows it.</summary>
        public const string Tracking = "Tracking";

        /// <summary>The tracked target hasn't been seen for a while; the UAV circles where it was.</summary>
        public const string TargetLost = "TargetLost";

        /// <summary>The tracked target is found again.</summary>
        public const string TargetRegained = "TargetRegained";

        /// <summary>A repeating search flew its whole route once and starts over.</summary>
        public const string PassCompleted = "PassCompleted";

        /// <summary>The tracked target couldn't be found again; the search route is resumed.</summary>
        public const string SearchResumed = "SearchResumed";
    }

    /// <summary>A command outcome an <see cref="IUavCommandHandler"/> implementation reports back
    /// — lets it fail a call (unknown tail number, hardware fault, etc.) instead of always having
    /// to fake success.</summary>
    public sealed class CommandResult<T>
    {
        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; }
        public T Value { get; private set; }

        private CommandResult(bool success, string errorMessage, T value)
        {
            Success = success;
            ErrorMessage = errorMessage;
            Value = value;
        }

        public static CommandResult<T> Ok(T value) => new CommandResult<T>(true, null, value);
        public static CommandResult<T> Fail(string errorMessage) => new CommandResult<T>(false, errorMessage, default(T));
    }
}

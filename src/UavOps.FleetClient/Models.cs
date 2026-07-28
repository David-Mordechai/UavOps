using System.Collections.Generic;

namespace UavOps.FleetClient
{
    // Net47-side copies of UavOps.Agent.Operations's models — no common TFM worth introducing for a
    // handful of tiny records. JSON property names must match UavOps.Agent's (camelCase)
    // by convention; FleetClientConnection is the only place that serializes/deserializes these.

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

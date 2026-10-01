using System;
using System.Collections.Generic;
using UavOps.FleetClient;

namespace UavOps.MockFleetClient
{
    /// <summary>
    /// Proves the SignalR plumbing works end to end — logs each command it receives and returns
    /// a hardcoded, correctly-shaped placeholder result. Deliberately does NOT simulate fleet
    /// state (position, speed, mission progress, etc.); that's already covered by
    /// UavOps.Agent's Simulation/ services. A real fleet-commanding app implements
    /// IUavCommandHandler with real hardware calls in place of this.
    /// </summary>
    public sealed class EmptyCommandHandler : IUavCommandHandler, IUavMissionHandler, IUavPayloadZoomHandler
    {
        private static TelemetrySnapshot DummySnapshot()
        {
            return new TelemetrySnapshot
            {
                Lat = 31.344000,
                Lng = 35.035000,
                SpeedKts = 100,
                AltitudeFt = 4000,
                Mode = "Orbiting",
                PayloadLockedOn = null,
                PayloadZoom = 1,
                PayloadHfovDeg = 40
            };
        }

        private static void Log(string command, string args)
        {
            Console.WriteLine("[{0:HH:mm:ss}] {1}({2})", DateTime.Now, command, args);
        }

        public CommandResult<List<UavSummary>> ListFleet()
        {
            Log("ListFleet", "");
            return CommandResult<List<UavSummary>>.Ok(new List<UavSummary>
            {
                new UavSummary { TailNumber = "997", Mode = "Orbiting", Lat = 31.344000, Lng = 35.035000 }
            });
        }

        public CommandResult<TelemetrySnapshot> GetTelemetry(string tailNumber)
        {
            Log("GetTelemetry", tailNumber);
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> Navigate(string tailNumber, string location)
        {
            Log("Navigate", string.Format("{0}, {1}", tailNumber, location));
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> SetSpeed(string tailNumber, int speedKts)
        {
            Log("SetSpeed", string.Format("{0}, {1}", tailNumber, speedKts));
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> SetAltitude(string tailNumber, int altitudeFt)
        {
            Log("SetAltitude", string.Format("{0}, {1}", tailNumber, altitudeFt));
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> ReturnToLaunch(string tailNumber)
        {
            Log("ReturnToLaunch", tailNumber);
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> PointPayload(string tailNumber, string location)
        {
            Log("PointPayload", string.Format("{0}, {1}", tailNumber, location));
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> ResetPayload(string tailNumber)
        {
            Log("ResetPayload", tailNumber);
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<TelemetrySnapshot> SetPayloadZoom(string tailNumber, double zoom)
        {
            Log("SetPayloadZoom", string.Format("{0}, {1}x", tailNumber, zoom));
            return CommandResult<TelemetrySnapshot>.Ok(DummySnapshot());
        }

        public CommandResult<int> UploadWaypoints(string tailNumber, List<Waypoint> waypoints)
        {
            Log("UploadWaypoints", string.Format("{0}, count={1}", tailNumber, waypoints.Count));
            return CommandResult<int>.Ok(waypoints.Count);
        }

        public CommandResult<MissionStatus> GetMissionStatus(string tailNumber)
        {
            Log("GetMissionStatus", tailNumber);
            return CommandResult<MissionStatus>.Ok(new MissionStatus { Mode = "Orbiting", WaypointCount = 0 });
        }

        public CommandResult<GdtLinkStatus> GetLinkStatus(string tailNumber)
        {
            Log("GetLinkStatus", tailNumber);
            return CommandResult<GdtLinkStatus>.Ok(new GdtLinkStatus { LinkState = "Connected", SignalStrengthPercent = 92, TrackingMode = "Auto" });
        }

        public CommandResult<GdtLinkStatus> SetTrackingMode(string tailNumber, string mode)
        {
            Log("SetTrackingMode", string.Format("{0}, {1}", tailNumber, mode));
            return CommandResult<GdtLinkStatus>.Ok(new GdtLinkStatus { LinkState = "Connected", SignalStrengthPercent = 92, TrackingMode = mode });
        }

        public CommandResult<MissionStatus> StartMission(string tailNumber)
        {
            Log("StartMission", tailNumber);
            return CommandResult<MissionStatus>.Ok(new MissionStatus { Mode = "Searching", WaypointCount = 0, CurrentWaypointIndex = 0 });
        }

        public CommandResult<MissionStatus> StopMission(string tailNumber)
        {
            Log("StopMission", tailNumber);
            return CommandResult<MissionStatus>.Ok(new MissionStatus { Mode = "Orbiting", WaypointCount = 0 });
        }

        private string _lastSearchTail = "997";
        private SearchTargetRequest _lastSearchTarget = new SearchTargetRequest
        {
            MissionId = "mock-mission",
            ZoneName = "ZoneA",
            Prompt = "white van",
            MinConfidence = 0.5
        };

        /// <summary>A made-up detection for the last search target set (or a white van in ZoneA),
        /// <paramref name="offsetMeters"/> north of a point inside ZoneA.</summary>
        public DetectionReport FakeDetection(double offsetMeters)
        {
            return new DetectionReport
            {
                TailNumber = _lastSearchTail,
                MissionId = _lastSearchTarget.MissionId,
                ZoneName = _lastSearchTarget.ZoneName,
                Prompt = _lastSearchTarget.Prompt,
                Label = _lastSearchTarget.Prompt,
                Confidence = 0.9,
                Lat = 31.80900 + offsetMeters / 111195.0,
                Lng = 34.66521,
                DetectedAtUtc = DateTime.UtcNow,
                TrackId = Guid.NewGuid().ToString("N").Substring(0, 8)
            };
        }

        public CommandResult<MissionStatus> SetSearchTarget(string tailNumber, SearchTargetRequest request)
        {
            _lastSearchTail = tailNumber;
            _lastSearchTarget = request;
            Log("SetSearchTarget", string.Format("{0}, mission={1}, zone={2}, prompt={3}", tailNumber, request.MissionId, request.ZoneName, request.Prompt));
            return CommandResult<MissionStatus>.Ok(new MissionStatus
            {
                Mode = "Orbiting",
                WaypointCount = 0,
                ActiveMissionId = request.MissionId,
                SearchPrompt = request.Prompt
            });
        }
    }
}

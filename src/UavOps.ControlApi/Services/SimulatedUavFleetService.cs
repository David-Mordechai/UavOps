using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

/// <summary>
/// In-memory, simulated <see cref="IUavFleetService"/> — stands in for real vehicle command/
/// telemetry until a real implementation (e.g. talking to actual autopilots) is available.
/// Explicitly named "Simulated" so the placeholder is never mistaken for the real thing.
/// </summary>
public sealed class SimulatedUavFleetService : IUavFleetService
{
    private sealed class VehicleState(double startLat, double startLng)
    {
        public readonly object Lock = new();
        public double Lat = startLat;
        public double Lng = startLng;
        public int SpeedKts = 105;
        public int AltitudeFt = 4000;
        public string Mode = "Orbiting";
        public string? PayloadLockedOn;
        public List<Waypoint> Waypoints = [];

        public TelemetrySnapshot Snapshot()
        {
            lock (Lock)
            {
                return new TelemetrySnapshot(Lat, Lng, SpeedKts, AltitudeFt, Mode, PayloadLockedOn);
            }
        }
    }

    private readonly Dictionary<string, VehicleState> _fleet;

    public SimulatedUavFleetService()
    {
        _fleet = new Dictionary<string, VehicleState>(StringComparer.OrdinalIgnoreCase)
        {
            ["UAV-1"] = new VehicleState(31.801447, 34.643497),
            ["UAV-2"] = new VehicleState(31.798000, 34.639000),
            ["UAV-3"] = new VehicleState(31.805000, 34.648000),
        };
    }

    public IReadOnlyCollection<UavSummary> ListFleet() =>
        _fleet.Select(kvp =>
        {
            var snap = kvp.Value.Snapshot();
            return new UavSummary(kvp.Key, snap.Mode, snap.Lat, snap.Lng);
        }).ToList();

    public UavCommandResult GetTelemetry(string tailNumber) =>
        _fleet.TryGetValue(tailNumber, out var v) ? UavCommandResult.Ok(v.Snapshot()) : UavCommandResult.NotFound(tailNumber);

    public UavCommandResult Navigate(string tailNumber, string location)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return UavCommandResult.NotFound(tailNumber);
        }

        if (!KnownPoints.TryResolve(location, out var lat, out var lng))
        {
            return UavCommandResult.Invalid($"Unknown location '{location}'.");
        }

        lock (v.Lock)
        {
            v.Lat = lat;
            v.Lng = lng;
            v.Mode = "Transiting";
        }

        return UavCommandResult.Ok(v.Snapshot());
    }

    public UavCommandResult SetSpeed(string tailNumber, int speedKts)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return UavCommandResult.NotFound(tailNumber);
        }

        if (speedKts is < 1 or > 500)
        {
            return UavCommandResult.Invalid("speedKts must be between 1 and 500.");
        }

        lock (v.Lock) { v.SpeedKts = speedKts; }
        return UavCommandResult.Ok(v.Snapshot());
    }

    public UavCommandResult SetAltitude(string tailNumber, int altitudeFt)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return UavCommandResult.NotFound(tailNumber);
        }

        if (altitudeFt is < 0 or > 60000)
        {
            return UavCommandResult.Invalid("altitudeFt must be between 0 and 60000.");
        }

        lock (v.Lock) { v.AltitudeFt = altitudeFt; }
        return UavCommandResult.Ok(v.Snapshot());
    }

    public UavCommandResult ReturnToLaunch(string tailNumber)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return UavCommandResult.NotFound(tailNumber);
        }

        lock (v.Lock) { v.Mode = "ReturningToLaunch"; }
        return UavCommandResult.Ok(v.Snapshot());
    }

    public UavCommandResult PointPayload(string tailNumber, string location)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return UavCommandResult.NotFound(tailNumber);
        }

        if (!KnownPoints.TryResolve(location, out _, out _))
        {
            return UavCommandResult.Invalid($"Unknown location '{location}'.");
        }

        lock (v.Lock) { v.PayloadLockedOn = location; }
        return UavCommandResult.Ok(v.Snapshot());
    }

    public UavCommandResult ResetPayload(string tailNumber)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return UavCommandResult.NotFound(tailNumber);
        }

        lock (v.Lock) { v.PayloadLockedOn = null; }
        return UavCommandResult.Ok(v.Snapshot());
    }

    public (bool Found, int Accepted) UploadWaypoints(string tailNumber, List<Waypoint> waypoints)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return (false, 0);
        }

        lock (v.Lock) { v.Waypoints = waypoints; }
        return (true, waypoints.Count);
    }

    public (bool Found, MissionStatus? Status) GetMissionStatus(string tailNumber)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return (false, null);
        }

        lock (v.Lock)
        {
            return (true, new MissionStatus(v.Mode, v.Waypoints.Count));
        }
    }
}

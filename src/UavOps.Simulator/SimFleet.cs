using UavOps.Agent.Mission;
using UavOps.FleetClient;
using KnownPoints = UavOps.Agent.Contracts.KnownPoints;

namespace UavOps.Simulator;

/// <summary>One simulated aircraft. Only touched under <see cref="SimFleet"/>'s lock.</summary>
public sealed class SimUav(string tailNumber, GeoPoint home)
{
    public string TailNumber { get; } = tailNumber;
    public GeoPoint Home { get; } = home;
    public GeoPoint Position { get; set; } = home;
    public double HeadingDeg { get; set; } = 90;
    public double SpeedKts { get; set; } = 105;
    public double TargetSpeedKts { get; set; } = 105;
    public double AltitudeFt { get; set; } = 4000;
    public double TargetAltitudeFt { get; set; } = 4000;
    public string Mode { get; set; } = "Orbiting";
    public string? PayloadLockedOn { get; set; }
    public string TrackingMode { get; set; } = "Auto";

    public GeoPoint OrbitCenter { get; set; } = home;
    public double OrbitAngle { get; set; }
    public GeoPoint? Destination { get; set; }

    public List<GeoPoint> Route { get; set; } = [];
    public int RouteAltitudeFt { get; set; }
    public int? WaypointIndex { get; set; }

    public string? MissionId { get; set; }
    public string? ZoneName { get; set; }
    public string? SearchPrompt { get; set; }
    public double MinConfidence { get; set; }

    /// <summary>Whether the onboard agent is looking for <see cref="SearchPrompt"/>: from when a
    /// target is set until the mission ends, while airborne.</summary>
    public bool IsLooking => SearchPrompt is not null && !SearchEnded && Mode != "Landed";
    public bool SearchEnded { get; set; }

    public LinkedList<GeoPoint> Trail { get; } = new();
    public double TrailClock { get; set; }
}

public sealed record SimTickResult(IReadOnlyList<DetectionReport> Detections, IReadOnlyList<MissionEventReport> MissionEvents);

/// <summary>
/// The simulated fleet: 997/998/999 at the same start points as McpMoav's in-memory backend,
/// flying for real - commands set where to go, and <see cref="Advance"/> moves every UAV at its
/// speed. Commands arrive on FleetClient's background threads and ticks on the tick loop, so
/// everything goes through one lock.
/// </summary>
public sealed class SimFleet
{
    private const double KnotsToMetersPerSecond = 0.514444;
    private const double SpeedChangeKtsPerSecond = 5;
    private const double ClimbFtPerSecond = 30;
    private const int TrailLength = 400;

    private readonly object _lock = new();
    private readonly Dictionary<string, SimUav> _uavs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MissionEventReport> _pendingEvents = [];
    private readonly List<DetectionReport> _detections = [];
    private readonly IOnboardDetector _detector;
    private readonly SimOptions _options;

    public SimFleet(IOnboardDetector detector, SimOptions options)
    {
        _detector = detector;
        _options = options;
        foreach (var (tail, lat, lng) in new[] { ("997", 31.801447, 34.643497), ("998", 31.798000, 34.639000), ("999", 31.805000, 34.648000) })
        {
            var uav = new SimUav(tail, new GeoPoint(lat, lng));
            HoldAt(uav, uav.Position);
            _uavs[tail] = uav;
        }
    }

    // ----- Time -----

    /// <summary>Moves every UAV <paramref name="seconds"/> of sim time forward, and returns what
    /// the fleet app should report to the host as a result.</summary>
    public SimTickResult Advance(double seconds, DateTime nowUtc)
    {
        var detections = new List<DetectionReport>();
        List<MissionEventReport> events;
        lock (_lock)
        {
            foreach (var uav in _uavs.Values)
            {
                Fly(uav, seconds);
                foreach (var detection in _detector.Look(uav, nowUtc))
                {
                    detections.Add(detection);
                    _detections.Add(detection);
                }
            }
            events = [.. _pendingEvents];
            _pendingEvents.Clear();
        }
        return new SimTickResult(detections, events);
    }

    private void Fly(SimUav uav, double seconds)
    {
        uav.SpeedKts = Approach(uav.SpeedKts, uav.TargetSpeedKts, SpeedChangeKtsPerSecond * seconds);
        uav.AltitudeFt = Approach(uav.AltitudeFt, uav.TargetAltitudeFt, ClimbFtPerSecond * seconds);
        var distance = uav.SpeedKts * KnotsToMetersPerSecond * seconds;

        switch (uav.Mode)
        {
            case "Transiting":
                if (FlyToward(uav, uav.Destination!.Value, distance, arrivalRadius: 0))
                    HoldAt(uav, uav.Position);
                break;

            case "ReturningToLaunch":
                if (FlyToward(uav, uav.Home, distance, arrivalRadius: 0))
                {
                    uav.Mode = "Landed";
                    uav.TargetSpeedKts = uav.SpeedKts = 0;
                    uav.TargetAltitudeFt = uav.AltitudeFt = 0;
                }
                break;

            case "Searching":
                // Several waypoints can be reached in one long (time-scaled) tick.
                while (distance > 0 && uav.WaypointIndex is { } index)
                {
                    var before = uav.Position;
                    if (!FlyToward(uav, uav.Route[index], distance, _options.ArrivalRadiusMeters))
                        break;
                    distance -= GeoProjection.DistanceMeters(before, uav.Position);
                    if (index + 1 < uav.Route.Count)
                    {
                        uav.WaypointIndex = index + 1;
                        continue;
                    }

                    uav.WaypointIndex = null;
                    uav.SearchEnded = true;
                    _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.Completed));
                    HoldAt(uav, uav.Position);
                }
                break;

            case "Orbiting":
                var radius = _options.OrbitRadiusMeters;
                uav.OrbitAngle += distance / radius;
                var local = new Vec2(Math.Cos(uav.OrbitAngle) * radius, Math.Sin(uav.OrbitAngle) * radius);
                uav.Position = new GeoProjection(uav.OrbitCenter).ToGeo(local);
                // Counter-clockwise: heading is 90° left of the radius (compass: 0 north, 90 east).
                uav.HeadingDeg = Normalize(90 - (uav.OrbitAngle * 180 / Math.PI + 90));
                break;
        }

        uav.TrailClock += seconds;
        if (uav.Mode != "Landed" && uav.TrailClock >= 1)
        {
            uav.TrailClock = 0;
            uav.Trail.AddLast(uav.Position);
            if (uav.Trail.Count > TrailLength)
                uav.Trail.RemoveFirst();
        }
    }

    /// <summary>Returns true on arrival (within <paramref name="arrivalRadius"/>, or reached this tick).</summary>
    private static bool FlyToward(SimUav uav, GeoPoint target, double distance, double arrivalRadius)
    {
        var projection = new GeoProjection(uav.Position);
        var toTarget = projection.ToLocal(target);
        var remaining = toTarget.Length;
        if (remaining > 0.01)
            uav.HeadingDeg = Normalize(Math.Atan2(toTarget.X, toTarget.Y) * 180 / Math.PI);

        if (remaining <= Math.Max(distance, arrivalRadius))
        {
            uav.Position = target;
            return true;
        }
        uav.Position = projection.ToGeo(toTarget * (distance / remaining));
        return false;
    }

    /// <summary>Start circling so the circle passes through <paramref name="point"/>, turning
    /// the way the UAV is already heading (no jump).</summary>
    private void HoldAt(SimUav uav, GeoPoint point)
    {
        uav.Mode = "Orbiting";
        uav.Destination = null;
        var heading = uav.HeadingDeg * Math.PI / 180;
        var toLeft = new Vec2(-Math.Cos(heading), Math.Sin(heading)) * _options.OrbitRadiusMeters;
        var projection = new GeoProjection(point);
        uav.OrbitCenter = projection.ToGeo(toLeft);
        uav.OrbitAngle = Math.Atan2(-toLeft.Y, -toLeft.X);
        uav.Position = point;
    }

    private static double Approach(double value, double target, double step) =>
        value < target ? Math.Min(value + step, target) : Math.Max(value - step, target);

    private static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;

    // ----- Sim control (the page's Reset button, not a fleet command) -----

    /// <summary>Every UAV back to its start point and starting state, detections cleared. A search
    /// in progress is reported as aborted, so the host's view of the mission stays right.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (var uav in _uavs.Values)
            {
                AbortSearch(uav);
                uav.Route = [];
                uav.RouteAltitudeFt = 0;
                uav.WaypointIndex = null;
                uav.MissionId = null;
                uav.ZoneName = null;
                uav.SearchPrompt = null;
                uav.SearchEnded = false;
                uav.PayloadLockedOn = null;
                uav.TrackingMode = "Auto";
                uav.SpeedKts = uav.TargetSpeedKts = 105;
                uav.AltitudeFt = uav.TargetAltitudeFt = 4000;
                uav.HeadingDeg = 90;
                uav.Trail.Clear();
                uav.TrailClock = 0;
                HoldAt(uav, uav.Home);
            }
            _detections.Clear();
        }
    }

    // ----- Commands (IUavCommandHandler/IUavMissionHandler, via SimulatorCommandHandler) -----

    public List<UavSummary> ListFleet()
    {
        lock (_lock)
            return _uavs.Values.Select(u => new UavSummary { TailNumber = u.TailNumber, Mode = u.Mode, Lat = u.Position.Lat, Lng = u.Position.Lng }).ToList();
    }

    public CommandResult<TelemetrySnapshot> GetTelemetry(string tail) => WithUav(tail, Telemetry);

    public CommandResult<TelemetrySnapshot> Navigate(string tail, string location) => WithUav(tail, uav =>
    {
        if (!KnownPoints.TryResolve(location, out var lat, out var lng))
            return CommandResult<TelemetrySnapshot>.Fail($"Unknown location '{location}'.");
        AbortSearch(uav);
        uav.Mode = "Transiting";
        uav.Destination = new GeoPoint(lat, lng);
        if (uav.TargetSpeedKts <= 0)
            uav.TargetSpeedKts = 105;
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> SetSpeed(string tail, int speedKts) => WithUav(tail, uav =>
    {
        if (speedKts is < 1 or > 500)
            return CommandResult<TelemetrySnapshot>.Fail("speedKts must be between 1 and 500.");
        uav.TargetSpeedKts = speedKts;
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> SetAltitude(string tail, int altitudeFt) => WithUav(tail, uav =>
    {
        if (altitudeFt is < 0 or > 60000)
            return CommandResult<TelemetrySnapshot>.Fail("altitudeFt must be between 0 and 60000.");
        uav.TargetAltitudeFt = altitudeFt;
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> ReturnToLaunch(string tail) => WithUav(tail, uav =>
    {
        AbortSearch(uav);
        uav.Mode = "ReturningToLaunch";
        uav.Destination = uav.Home;
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> PointPayload(string tail, string location) => WithUav(tail, uav =>
    {
        if (!KnownPoints.TryResolve(location, out _, out _))
            return CommandResult<TelemetrySnapshot>.Fail($"Unknown location '{location}'.");
        uav.PayloadLockedOn = location;
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> ResetPayload(string tail) => WithUav(tail, uav =>
    {
        uav.PayloadLockedOn = null;
        return Telemetry(uav);
    });

    public CommandResult<int> UploadWaypoints(string tail, List<Waypoint> waypoints) => WithUav(tail, uav =>
    {
        if (waypoints.Count == 0)
            return CommandResult<int>.Fail("The waypoint list is empty.");
        AbortSearch(uav);
        if (uav.Mode == "Searching")
            HoldAt(uav, uav.Position);
        uav.Route = waypoints.Select(w => new GeoPoint(w.Lat, w.Lng)).ToList();
        uav.RouteAltitudeFt = waypoints[0].AltitudeFt;
        uav.WaypointIndex = null;
        return CommandResult<int>.Ok(waypoints.Count);
    });

    public CommandResult<MissionStatus> GetMissionStatus(string tail) => WithUav(tail, uav => CommandResult<MissionStatus>.Ok(Mission(uav)));

    public CommandResult<GdtLinkStatus> GetLinkStatus(string tail) => WithUav(tail, uav =>
        CommandResult<GdtLinkStatus>.Ok(new GdtLinkStatus { LinkState = "Connected", SignalStrengthPercent = 92, TrackingMode = uav.TrackingMode }));

    public CommandResult<GdtLinkStatus> SetTrackingMode(string tail, string mode) => WithUav(tail, uav =>
    {
        if (!mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !mode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
            return CommandResult<GdtLinkStatus>.Fail("mode must be 'Auto' or 'Manual'.");
        uav.TrackingMode = mode;
        return CommandResult<GdtLinkStatus>.Ok(new GdtLinkStatus { LinkState = "Connected", SignalStrengthPercent = 92, TrackingMode = mode });
    });

    public CommandResult<MissionStatus> StartMission(string tail) => WithUav(tail, uav =>
    {
        if (uav.Route.Count == 0)
            return CommandResult<MissionStatus>.Fail($"{tail} has no route uploaded to fly.");
        uav.Mode = "Searching";
        uav.Destination = null;
        uav.WaypointIndex = 0;
        uav.TargetAltitudeFt = uav.RouteAltitudeFt;
        uav.MissionId ??= $"mission-{tail}-{Guid.NewGuid().ToString("N")[..6]}";
        uav.SearchEnded = false;
        if (uav.TargetSpeedKts <= 0)
            uav.TargetSpeedKts = 105;
        return CommandResult<MissionStatus>.Ok(Mission(uav));
    });

    public CommandResult<MissionStatus> SetSearchTarget(string tail, SearchTargetRequest request) => WithUav(tail, uav =>
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return CommandResult<MissionStatus>.Fail("The search target description is empty.");
        uav.MissionId = request.MissionId;
        uav.ZoneName = request.ZoneName;
        uav.SearchPrompt = request.Prompt.Trim();
        uav.MinConfidence = request.MinConfidence;
        uav.SearchEnded = false;
        return CommandResult<MissionStatus>.Ok(Mission(uav));
    });

    private void AbortSearch(SimUav uav)
    {
        if (uav.Mode != "Searching")
            return;
        uav.WaypointIndex = null;
        uav.SearchEnded = true;
        _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.Aborted));
    }

    private static MissionEventReport MissionEvent(SimUav uav, string kind) => new()
    {
        TailNumber = uav.TailNumber,
        MissionId = uav.MissionId ?? "",
        ZoneName = uav.ZoneName ?? "",
        Kind = kind
    };

    private CommandResult<T> WithUav<T>(string tail, Func<SimUav, CommandResult<T>> action)
    {
        lock (_lock)
        {
            return _uavs.TryGetValue(tail, out var uav)
                ? action(uav)
                : CommandResult<T>.Fail($"Unknown UAV '{tail}'.");
        }
    }

    private static CommandResult<TelemetrySnapshot> Telemetry(SimUav uav) => CommandResult<TelemetrySnapshot>.Ok(new TelemetrySnapshot
    {
        Lat = Math.Round(uav.Position.Lat, 6),
        Lng = Math.Round(uav.Position.Lng, 6),
        SpeedKts = (int)Math.Round(uav.SpeedKts),
        AltitudeFt = (int)Math.Round(uav.AltitudeFt),
        Mode = uav.Mode,
        PayloadLockedOn = uav.PayloadLockedOn
    });

    private static MissionStatus Mission(SimUav uav) => new()
    {
        Mode = uav.Mode,
        WaypointCount = uav.Route.Count,
        CurrentWaypointIndex = uav.WaypointIndex,
        ActiveMissionId = uav.MissionId,
        SearchPrompt = uav.SearchPrompt
    };

    // ----- For the page -----

    public SimFleetView View(Func<double, double> footprintRadiusMeters)
    {
        lock (_lock)
        {
            return new SimFleetView(
                _uavs.Values.Select(u => new SimUavView(
                    u.TailNumber, u.Position.Lat, u.Position.Lng, Math.Round(u.HeadingDeg, 1),
                    (int)Math.Round(u.SpeedKts), (int)Math.Round(u.AltitudeFt), u.Mode,
                    u.Destination is { } d ? [d.Lng, d.Lat] : null,
                    u.Route.Select(p => new[] { p.Lng, p.Lat }).ToList(),
                    u.WaypointIndex, u.MissionId, u.ZoneName, u.SearchPrompt, u.IsLooking,
                    Math.Round(footprintRadiusMeters(u.AltitudeFt)),
                    u.Trail.Select(p => new[] { p.Lng, p.Lat }).ToList())).ToList(),
                _detections.TakeLast(50).Select(d => new SimDetectionView(d.TailNumber, d.Prompt, d.Label, d.Confidence, d.Lat, d.Lng, d.DetectedAtUtc)).ToList());
        }
    }
}

public sealed record SimUavView(
    string TailNumber, double Lat, double Lng, double HeadingDeg, int SpeedKts, int AltitudeFt, string Mode,
    double[]? Destination, List<double[]> Route, int? WaypointIndex, string? MissionId, string? ZoneName,
    string? SearchPrompt, bool Looking, double FootprintRadiusMeters, List<double[]> Trail);

public sealed record SimDetectionView(string TailNumber, string Prompt, string Label, double Confidence, double Lat, double Lng, DateTime DetectedAtUtc);

public sealed record SimFleetView(List<SimUavView> Uavs, List<SimDetectionView> Detections);

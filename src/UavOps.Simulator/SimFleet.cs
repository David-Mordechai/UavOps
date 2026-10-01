using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Onboard.Contracts;
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
    /// <summary>Where the payload is locked, as commanded (a known point or "lat,lng"), and that
    /// point resolved. Null: the camera looks straight down.</summary>
    public string? PayloadLockedOn { get; set; }
    public GeoPoint? PayloadTarget { get; set; }

    /// <summary>The payload camera's zoom (1 = widest) and the horizontal field of view it gives;
    /// changed only by SetPayloadZoom.</summary>
    public double PayloadZoom { get; set; } = 1;
    public double PayloadHfovDeg { get; set; }
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

    /// <summary>Circling at the route's first waypoint until at search altitude, before flying it.</summary>
    public bool EntryHold { get; set; }

    /// <summary>The route is flown, but the search isn't over until the onboard agent has looked
    /// at every frame (<see cref="IOnboardDetector.HasFinished"/>).</summary>
    public bool RouteFlown { get; set; }

    public LinkedList<GeoPoint> Trail { get; } = new();
    public double TrailClock { get; set; }

    /// <summary>Find and track: once the onboard computer has found and verified the target, it
    /// locks the payload on it and reports where it is; the UAV then circles that position.</summary>
    public bool TrackTarget { get; set; }

    /// <summary>Fly the route again and again until the mission is stopped; which pass this is.</summary>
    public bool RepeatSearch { get; set; }
    public int SearchPass { get; set; }

    /// <summary>The payload zoom the search was started with (the ground side set it for the lanes'
    /// spacing): restored whenever the UAV goes back to its route after following a target.</summary>
    public double SearchZoom { get; set; } = 1;

    /// <summary>The target being followed, as the onboard computer last reported it.</summary>
    public GeoPoint? FollowCenter { get; set; }
    public string? FollowState { get; set; }
    public string? FollowTrackId { get; set; }
    public string? FollowLabel { get; set; }
    public GeoPoint? FollowForwarded { get; set; }
    public DateTime FollowForwardedAtUtc { get; set; }

    /// <summary>The followed target's velocity (m/s east, north) and when the frame it was seen in
    /// was captured: the page's ring is drawn where it is now, not where it was in that frame.</summary>
    public Vec2 FollowVelocity { get; set; }
    public DateTime FollowSeenAtUtc { get; set; }

    /// <summary>The live video's last frame number (for the onboard computer's every-frame loop).</summary>
    public long VideoSeq { get; set; }

    /// <summary>The camera's last survey frame number; never reset, so frame numbers only grow.</summary>
    public long LastFrameSeq { get; set; }
    /// <summary>Distance flown since the last survey frame, or null before the first one of a search.</summary>
    public double? SinceLastFrameMeters { get; set; }
}

public sealed record SurveyCapture(string TailNumber, FrameTelemetry Telemetry);

public sealed record SimTickResult(
    IReadOnlyList<DetectionReport> Detections,
    IReadOnlyList<MissionEventReport> MissionEvents,
    IReadOnlyList<SurveyCapture> SurveyCaptures);

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
    private const double AltitudeToleranceFt = 30;

    /// <summary>A followed target's position goes to the ground (as a detection update under its
    /// track id) when it has moved this far, or at least this often while it moves: the model's
    /// history gets each one, so not every second.</summary>
    private const double FollowForwardMeters = 50;
    private static readonly TimeSpan FollowForwardInterval = TimeSpan.FromSeconds(20);

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
        // Same start points as McpMoav's in-memory backend: 997 and 998 at the base by ZoneA
        // (Yatir), and 999 there too.
        foreach (var (tail, lat, lng) in new[] { ("997", 31.344000, 35.035000), ("998", 31.342500, 35.033500), ("999", 31.345500, 35.036500) })
        {
            var uav = new SimUav(tail, new GeoPoint(lat, lng));
            Zoom(uav, 1);
            HoldAt(uav, uav.Position);
            _uavs[tail] = uav;
        }
    }

    // ----- Time -----

    /// <summary>Moves every UAV <paramref name="seconds"/> of sim time forward, and returns what
    /// the fleet app should report to the host as a result, and the survey frames its cameras took.</summary>
    public SimTickResult Advance(double seconds, DateTime nowUtc)
    {
        var detections = new List<DetectionReport>();
        var captures = new List<SurveyCapture>();
        List<MissionEventReport> events;
        lock (_lock)
        {
            foreach (var uav in _uavs.Values)
            {
                FlyAndSurvey(uav, seconds, nowUtc, captures);
                foreach (var detection in _detector.Look(uav, nowUtc))
                {
                    detections.Add(detection);
                    _detections.Add(detection);
                }
                while (_detector.TakeTrack(uav) is { } report)
                    ApplyTrack(uav, report, detections);
                // Complete once the route is flown and every frame has been looked at: reporting
                // "nothing found" while frames still wait would be wrong.
                if (uav.RouteFlown && !uav.SearchEnded && _detector.HasFinished(uav))
                {
                    uav.RouteFlown = false;
                    uav.SearchEnded = true;
                    _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.Completed));
                }
            }
            events = [.. _pendingEvents];
            _pendingEvents.Clear();
        }
        return new SimTickResult(detections, events, captures);
    }

    /// <summary>
    /// While a search mission is flying (a target set, <c>Searching</c>), the camera takes a survey
    /// frame every (1 − overlap) × frame height flown. Not before: frames taken on the way to the
    /// zone, thousands of feet up, would only cost model time. A long tick (a high time scale) is
    /// flown in steps of at most that distance, so no stretch of ground is skipped however fast the
    /// sim runs.
    /// </summary>
    private void FlyAndSurvey(SimUav uav, double seconds, DateTime nowUtc, List<SurveyCapture> captures)
    {
        if (!Surveying(uav))
        {
            uav.SinceLastFrameMeters = null;
            Fly(uav, seconds);
            return;
        }

        var remaining = seconds;
        while (remaining > 1e-9)
        {
            var spacing = SurveySpacingMeters(uav);
            // Before the first frame of a search, one is due straight away.
            var since = uav.SinceLastFrameMeters ?? spacing;
            if (since >= spacing - 0.01)
            {
                uav.SinceLastFrameMeters = since = 0;
                uav.LastFrameSeq++;
                captures.Add(new SurveyCapture(uav.TailNumber, Camera(uav, uav.LastFrameSeq, nowUtc)));
            }

            // Fly exactly to the next frame's point (or the end of the tick), measured along the
            // path flown: a frame taken at the end of a longer step would overshoot its spacing
            // and leave a strip of ground unphotographed (measured: 108 m apart for an 81 m frame).
            var speed = Math.Max(uav.SpeedKts, 1) * KnotsToMetersPerSecond;
            var step = Math.Min(remaining, (spacing - since) / speed);
            Fly(uav, step);
            remaining -= step;
            if (!Surveying(uav))
            {
                uav.SinceLastFrameMeters = null;
                Fly(uav, remaining);
                break;
            }
            uav.SinceLastFrameMeters = since + speed * step;
        }
    }

    private static bool Surveying(SimUav uav) =>
        uav.IsLooking && uav.Mode == "Searching" && !uav.EntryHold && uav.WaypointIndex is > 0;

    private double SurveySpacingMeters(SimUav uav)
    {
        var height = CameraModel.GroundWidthFor(uav.AltitudeFt, uav.PayloadHfovDeg) * _options.CameraHeight / _options.CameraWidth;
        return Math.Max(height * (1 - Math.Clamp(_options.SurveyFrameOverlap, 0, 0.9)), 10);
    }

    /// <summary>The camera straight down under the UAV at its commanded zoom: what survey frames
    /// are taken with.</summary>
    private FrameTelemetry Camera(SimUav uav, long seq, DateTime nowUtc) => new(
        seq, nowUtc, uav.Position.Lat, uav.Position.Lng, Math.Max(uav.AltitudeFt, 1), uav.HeadingDeg,
        uav.PayloadHfovDeg, _options.CameraWidth, _options.CameraHeight, uav.IsLooking ? uav.MissionId : null);

    /// <summary>
    /// Where the payload is actually looking: locked on a point (PointPayload, and in reach), the
    /// gimbal holds that point in the centre of the picture; otherwise straight down. Either way at
    /// the zoom SetPayloadZoom set - pointing never zooms. Survey frames stay straight down while a
    /// search route is flown, since the lanes are planned for that.
    /// </summary>
    private FrameTelemetry PayloadCamera(SimUav uav, long seq, DateTime nowUtc)
    {
        var nadir = Camera(uav, seq, nowUtc);
        if (uav.PayloadTarget is not { } target || Surveying(uav)
            || GeoProjection.DistanceMeters(uav.Position, target) > _options.MaxZoomRangeMeters)
            return nadir;
        // North-up: drawn heading-up, the picture would spin around the target once per orbit.
        return nadir with { Lat = target.Lat, Lng = target.Lng, HeadingDeg = 0 };
    }

    /// <summary>The point the payload is locked on, while it holds it (in reach, not surveying).</summary>
    private double[]? LookAt(SimUav uav) =>
        uav.PayloadTarget is { } t && !Surveying(uav) && GeoProjection.DistanceMeters(uav.Position, t) <= _options.MaxZoomRangeMeters
            ? [t.Lng, t.Lat]
            : null;

    private void Zoom(SimUav uav, double zoom)
    {
        uav.PayloadZoom = Math.Clamp(zoom, 1, Math.Max(_options.PayloadMaxZoom, 1));
        uav.PayloadHfovDeg = _options.PayloadWideHorizontalFovDeg / uav.PayloadZoom;
    }

    /// <summary>What the UAV's payload is looking at right now, for the live view; or, with
    /// <paramref name="nadir"/>, the camera straight under the UAV (where the UAV is).</summary>
    public FrameTelemetry? CameraNow(string tail, DateTime nowUtc, bool nadir = false)
    {
        lock (_lock)
        {
            if (!_uavs.TryGetValue(tail, out var uav))
                return null;
            return nadir ? Camera(uav, uav.LastFrameSeq, nowUtc) : PayloadCamera(uav, uav.LastFrameSeq, nowUtc);
        }
    }

    // ----- The onboard computer (UavOps.Onboard.Detector on the Jetson) -----

    /// <summary>UAVs whose onboard agent is looking now.</summary>
    public List<string> Looking()
    {
        lock (_lock)
            return _uavs.Values.Where(u => u.IsLooking).Select(u => u.TailNumber).ToList();
    }

    /// <summary>The next live-video frame of a looking UAV's payload (where it points, at its zoom),
    /// numbered in its own sequence; null if it isn't looking.</summary>
    public FrameTelemetry? NextVideoFrame(string tail, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_uavs.TryGetValue(tail, out var uav) || !uav.IsLooking)
                return null;
            uav.VideoSeq++;
            return PayloadCamera(uav, uav.VideoSeq, nowUtc);
        }
    }

    /// <summary>The onboard computer aims the payload at a ground point (holding a target in view).</summary>
    public bool OnboardPoint(string tail, double lat, double lng) => Onboard(tail, uav =>
        LockPayload(uav, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{lat:F5},{lng:F5}"), new GeoPoint(lat, lng)));

    /// <summary>The onboard computer zooms so the frame shows about this much ground across.</summary>
    public bool OnboardZoom(string tail, double groundWidthMeters) => Onboard(tail, uav =>
    {
        var wide = CameraModel.GroundWidthFor(Math.Max(uav.AltitudeFt, 1), _options.PayloadWideHorizontalFovDeg);
        Zoom(uav, wide / Math.Max(groundWidthMeters, 1));
    });

    /// <summary>
    /// The onboard computer lets go of the payload: back to straight down. The zoom goes back to the
    /// search's only while a search is flying; otherwise it's left alone - a finished task's release
    /// can arrive after the next search has already zoomed for its lanes (measured: it reset that
    /// new search to 1x, where a car is ~6 px), and ending a mission resets the payload by itself.
    /// </summary>
    public bool OnboardRelease(string tail) => Onboard(tail, uav =>
    {
        LockPayload(uav, null, null);
        if (uav.IsLooking && uav.Mode == "Searching")
            Zoom(uav, uav.SearchZoom);
    });

    private bool Onboard(string tail, Action<SimUav> action)
    {
        lock (_lock)
        {
            if (!_uavs.TryGetValue(tail, out var uav))
                return false;
            action(uav);
            return true;
        }
    }

    /// <summary>Detections this UAV reported in its current (or last) search.</summary>
    public List<DetectionReport> DetectionsOf(string tail)
    {
        lock (_lock)
        {
            if (!_uavs.TryGetValue(tail, out var uav) || uav.MissionId is null)
                return [];
            return _detections.Where(d => d.TailNumber == uav.TailNumber && d.MissionId == uav.MissionId).ToList();
        }
    }

    private void Fly(SimUav uav, double seconds)
    {
        uav.SpeedKts = Approach(uav.SpeedKts, uav.TargetSpeedKts, SpeedChangeKtsPerSecond * seconds);
        uav.AltitudeFt = Approach(uav.AltitudeFt, uav.TargetAltitudeFt, ClimbFtPerSecond * seconds);
        var distance = uav.SpeedKts * KnotsToMetersPerSecond * seconds;

        switch (uav.Mode)
        {
            case "Transiting":
            {
                // Sent to a point: loiter around it (the circle centred on it, so it stays in view),
                // not over it and off.
                var destination = uav.Destination!.Value;
                var toCircle = GeoProjection.DistanceMeters(uav.Position, destination) - _options.OrbitRadiusMeters;
                if (toCircle > distance)
                {
                    FlyToward(uav, destination, distance, arrivalRadius: 0);
                    break;
                }
                if (toCircle > 0)
                    FlyToward(uav, destination, toCircle, arrivalRadius: 0);
                LoiterAround(uav, destination);
                Circle(uav, Math.Max(distance - Math.Max(toCircle, 0), 0));
                break;
            }

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
                    if (uav.EntryHold)
                    {
                        // At search altitude: back to the first waypoint, then fly the route.
                        if (Math.Abs(uav.AltitudeFt - uav.TargetAltitudeFt) <= AltitudeToleranceFt)
                        {
                            uav.EntryHold = false;
                            continue;
                        }
                        Circle(uav, distance);
                        break;
                    }

                    var before = uav.Position;
                    if (!FlyToward(uav, uav.Route[index], distance, _options.ArrivalRadiusMeters))
                        break;
                    distance -= GeoProjection.DistanceMeters(before, uav.Position);
                    if (index == 0 && Math.Abs(uav.AltitudeFt - uav.TargetAltitudeFt) > AltitudeToleranceFt)
                    {
                        // Too high (or low) to search yet: circle here until at altitude.
                        uav.EntryHold = true;
                        SetOrbitThrough(uav, uav.Position);
                        continue;
                    }
                    if (index + 1 < uav.Route.Count)
                    {
                        uav.WaypointIndex = index + 1;
                        continue;
                    }

                    if (uav.RepeatSearch && uav.Route.Count > 1 && !uav.SearchEnded)
                    {
                        // Again, from this end: the route backwards, so the next pass starts right
                        // here instead of flying back across the zone to the first waypoint.
                        uav.Route.Reverse();
                        uav.WaypointIndex = 1;
                        uav.SearchPass++;
                        _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.PassCompleted));
                        continue;
                    }
                    uav.WaypointIndex = null;
                    uav.RouteFlown = true;
                    HoldAt(uav, uav.Position);
                }
                break;

            case "Orbiting":
                Circle(uav, distance);
                break;

            case "Following":
                FollowCircle(uav, distance);
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

    private void Circle(SimUav uav, double distance)
    {
        var radius = _options.OrbitRadiusMeters;
        uav.OrbitAngle += distance / radius;
        var local = new Vec2(Math.Cos(uav.OrbitAngle) * radius, Math.Sin(uav.OrbitAngle) * radius);
        uav.Position = new GeoProjection(uav.OrbitCenter).ToGeo(local);
        // Counter-clockwise: heading is 90° left of the radius (compass: 0 north, 90 east).
        uav.HeadingDeg = Normalize(90 - (uav.OrbitAngle * 180 / Math.PI + 90));
    }

    /// <summary>
    /// Circles a moving point (the tracked target): flies to it when far, otherwise round it
    /// counter-clockwise, easing back onto the orbit radius as the centre moves - no jumps, unlike
    /// <see cref="Circle"/>, whose centre is fixed.
    /// </summary>
    private void FollowCircle(SimUav uav, double distance)
    {
        if (uav.FollowCenter is not { } center)
        {
            Circle(uav, distance);
            return;
        }
        var radius = _options.OrbitRadiusMeters;
        var projection = new GeoProjection(center);
        var local = projection.ToLocal(uav.Position);
        var r = local.Length;
        if (r > radius * 1.5)
        {
            FlyToward(uav, center, Math.Min(distance, r - radius), arrivalRadius: 0);
            return;
        }
        if (r < 1)
            local = new Vec2(0, -radius);
        var angle = Math.Atan2(local.Y, local.X) + distance / radius;
        var newR = Math.Max(r, 1) + Math.Clamp(radius - r, -distance * 0.5, distance * 0.5);
        var next = new Vec2(Math.Cos(angle) * newR, Math.Sin(angle) * newR);
        var step = next - local;
        if (step.Length > 0.01)
            uav.HeadingDeg = Normalize(Math.Atan2(step.X, step.Y) * 180 / Math.PI);
        uav.Position = projection.ToGeo(next);
    }

    /// <summary>
    /// A report from the onboard computer about the target it tracks. The first one switches the
    /// UAV from its search route to following (the mission goes on, as tracking); every one moves
    /// the circle's centre. The ground hears the lock, a loss and a regain as mission events, and the
    /// target's position as detection updates under its track id - every
    /// <see cref="FollowForwardMeters"/> or <see cref="FollowForwardInterval"/>, not every second.
    /// </summary>
    private void ApplyTrack(SimUav uav, TargetTrackReport report, List<DetectionReport> detections)
    {
        if (report.MissionId != uav.MissionId || !uav.IsLooking)
            return;
        var at = new GeoPoint(report.Lat, report.Lng);
        if (report.State == TargetTrackStates.Released)
        {
            if (uav.Mode == "Following")
                ResumeSearch(uav);
            return;
        }
        var previous = uav.FollowState;
        uav.FollowCenter = at;
        var heading = (report.HeadingDeg ?? 0) * Math.PI / 180;
        uav.FollowVelocity = report.HeadingDeg is null ? new Vec2(0, 0) : new Vec2(Math.Sin(heading), Math.Cos(heading)) * report.SpeedMps;
        uav.FollowSeenAtUtc = report.SeenAtUtc;
        uav.FollowState = report.State;
        uav.FollowTrackId = report.TrackId;
        uav.FollowLabel = report.Label;
        if (uav.Mode != "Following")
        {
            uav.Mode = "Following";
            uav.Destination = null;
            uav.WaypointIndex = null;
            uav.EntryHold = false;
            uav.RouteFlown = false;
            _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.Tracking));
        }
        else if (report.State == TargetTrackStates.Lost && previous != TargetTrackStates.Lost)
            _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.TargetLost));
        else if (report.State == TargetTrackStates.Tracking && previous == TargetTrackStates.Lost)
            _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.TargetRegained));

        if (report.State == TargetTrackStates.Lost)
            return;
        var due = uav.FollowForwarded is not { } last
                  || GeoProjection.DistanceMeters(last, at) >= FollowForwardMeters
                  || (report.SeenAtUtc - uav.FollowForwardedAtUtc >= FollowForwardInterval && GeoProjection.DistanceMeters(last, at) >= 5);
        if (!due)
            return;
        uav.FollowForwarded = at;
        uav.FollowForwardedAtUtc = report.SeenAtUtc;
        var update = new DetectionReport
        {
            TailNumber = uav.TailNumber,
            MissionId = report.MissionId,
            ZoneName = report.ZoneName,
            Prompt = report.Prompt,
            Label = report.Label,
            Confidence = report.Confidence,
            Lat = report.Lat,
            Lng = report.Lng,
            DetectedAtUtc = report.SeenAtUtc,
            TrackId = report.TrackId
        };
        detections.Add(update);
        _detections.Add(update);
    }

    /// <summary>Start circling so the circle passes through <paramref name="point"/>, turning
    /// the way the UAV is already heading (no jump).</summary>
    private void HoldAt(SimUav uav, GeoPoint point)
    {
        uav.Mode = "Orbiting";
        uav.Destination = null;
        SetOrbitThrough(uav, point);
    }

    /// <summary>Start circling around <paramref name="center"/> from where the UAV is. The payload
    /// is left alone: it moves only on PointPayload/ResetPayload.</summary>
    private static void LoiterAround(SimUav uav, GeoPoint center)
    {
        uav.Mode = "Orbiting";
        uav.Destination = null;
        var fromCenter = new GeoProjection(center).ToLocal(uav.Position);
        if (fromCenter.Length < 1)
            fromCenter = new Vec2(0, -1);
        uav.OrbitCenter = center;
        uav.OrbitAngle = Math.Atan2(fromCenter.Y, fromCenter.X);
    }

    private static void LockPayload(SimUav uav, string? location, GeoPoint? target)
    {
        uav.PayloadLockedOn = location;
        uav.PayloadTarget = target;
    }

    private void SetOrbitThrough(SimUav uav, GeoPoint point)
    {
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
                uav.TrackTarget = false;
                ClearFollow(uav);
                uav.SearchEnded = false;
                uav.RouteFlown = false;
                uav.EntryHold = false;
                LockPayload(uav, null, null);
                Zoom(uav, 1);
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
        if (!KnownPoints.TryResolve(location, out var lat, out var lng))
            return CommandResult<TelemetrySnapshot>.Fail($"Unknown location '{location}'.");
        LockPayload(uav, location, new GeoPoint(lat, lng));
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> ResetPayload(string tail) => WithUav(tail, uav =>
    {
        LockPayload(uav, null, null);
        return Telemetry(uav);
    });

    public CommandResult<TelemetrySnapshot> SetPayloadZoom(string tail, double zoom) => WithUav(tail, uav =>
    {
        if (double.IsNaN(zoom) || zoom <= 0)
            return CommandResult<TelemetrySnapshot>.Fail("zoom must be a positive number (1 = widest).");
        Zoom(uav, zoom);
        return Telemetry(uav);
    });

    public CommandResult<int> UploadWaypoints(string tail, List<Waypoint> waypoints) => WithUav(tail, uav =>
    {
        if (waypoints.Count == 0)
            return CommandResult<int>.Fail("The waypoint list is empty.");
        CloseDrainingSearch(uav);
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
        LockPayload(uav, null, null); // the search looks straight down
        uav.SearchZoom = uav.PayloadZoom;
        uav.WaypointIndex = 0;
        uav.EntryHold = false;
        uav.RouteFlown = false;
        uav.TargetAltitudeFt = uav.RouteAltitudeFt;
        uav.MissionId ??= $"mission-{tail}-{Guid.NewGuid().ToString("N")[..6]}";
        uav.SearchEnded = false;
        if (uav.TargetSpeedKts <= 0)
            uav.TargetSpeedKts = 105;
        return CommandResult<MissionStatus>.Ok(Mission(uav));
    });

    /// <summary>
    /// The operator's "stop the search" / "stop tracking": the mission ends (reported aborted, as a
    /// redirect would be), the UAV circles where it is, and the payload goes back to straight down
    /// at its widest (the onboard computer, told the search is over, lets go too).
    /// </summary>
    public CommandResult<MissionStatus> StopMission(string tail) => WithUav(tail, uav =>
    {
        if (uav.Mode is "Searching" or "Following")
            AbortSearch(uav);
        else if (uav.SearchPrompt is not null && !uav.SearchEnded)
        {
            uav.SearchEnded = true;
            _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.Aborted));
        }
        uav.WaypointIndex = null;
        uav.RouteFlown = false;
        uav.EntryHold = false;
        ClearFollow(uav);
        LockPayload(uav, null, null);
        Zoom(uav, 1);
        HoldAt(uav, uav.Position);
        return CommandResult<MissionStatus>.Ok(Mission(uav));
    });

    public CommandResult<MissionStatus> SetSearchTarget(string tail, SearchTargetRequest request) => WithUav(tail, uav =>
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return CommandResult<MissionStatus>.Fail("The search target description is empty.");
        CloseDrainingSearch(uav);
        uav.MissionId = request.MissionId;
        uav.ZoneName = request.ZoneName;
        uav.SearchPrompt = request.Prompt.Trim();
        uav.MinConfidence = request.MinConfidence;
        uav.TrackTarget = request.Track;
        uav.RepeatSearch = request.Repeat;
        uav.SearchPass = 1;
        uav.SearchEnded = false;
        ClearFollow(uav);
        return CommandResult<MissionStatus>.Ok(Mission(uav));
    });

    /// <summary>
    /// A search whose route is flown stays open until the onboard agent has looked at every frame.
    /// A new search set up in the meantime would take over its mission id, and the old search's
    /// end was then reported as the new one's ("no red car found" for a white-pickup search). So the
    /// old one is closed first, under its own id: completed if every frame was looked at, otherwise
    /// aborted (the rest of its frames won't be).
    /// </summary>
    private void CloseDrainingSearch(SimUav uav)
    {
        if (!uav.RouteFlown || uav.SearchEnded)
            return;
        var finished = _detector.HasFinished(uav);
        uav.RouteFlown = false;
        uav.SearchEnded = true;
        _pendingEvents.Add(MissionEvent(uav, finished ? MissionEventKinds.Completed : MissionEventKinds.Aborted));
    }

    private void AbortSearch(SimUav uav)
    {
        if (uav.Mode is not ("Searching" or "Following"))
            return;
        uav.WaypointIndex = null;
        uav.SearchEnded = true;
        ClearFollow(uav);
        _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.Aborted));
    }

    /// <summary>The onboard computer gave the target up: back onto the search route from its
    /// nearest waypoint, the search still repeating.</summary>
    private void ResumeSearch(SimUav uav)
    {
        ClearFollow(uav);
        if (uav.Route.Count < 2)
        {
            HoldAt(uav, uav.Position);
            return;
        }
        var nearest = Enumerable.Range(1, uav.Route.Count - 1)
            .MinBy(i => GeoProjection.DistanceMeters(uav.Position, uav.Route[i]));
        // Straight down at the search zoom again: following moved and zoomed the payload, and a
        // search at another zoom sees nothing (at 1x a car is ~6 px - measured: the operator saw the
        // red car on screen, the detector couldn't).
        LockPayload(uav, null, null);
        Zoom(uav, uav.SearchZoom);
        uav.Mode = "Searching";
        uav.WaypointIndex = nearest;
        uav.EntryHold = false;
        uav.RouteFlown = false;
        _pendingEvents.Add(MissionEvent(uav, MissionEventKinds.SearchResumed));
    }

    /// <summary>
    /// Where the followed target is now: its last report moved on by its reported velocity for the
    /// time since that frame was captured (at most <see cref="MaxFollowPredictSeconds"/>). Reports
    /// come about once a second, a frame's processing late, so the ring drawn at the report itself
    /// trailed the moving car and jumped after it.
    /// </summary>
    private static GeoPoint? FollowNow(SimUav uav)
    {
        // Coasting or lost, the report is already the tracker's own prediction, not a sighting.
        if (uav.FollowCenter is not { } center || uav.FollowState != TargetTrackStates.Tracking)
            return uav.FollowCenter;
        var age = Math.Clamp((DateTime.UtcNow - uav.FollowSeenAtUtc).TotalSeconds, 0, MaxFollowPredictSeconds);
        return new GeoProjection(center).ToGeo(uav.FollowVelocity * age);
    }

    private const double MaxFollowPredictSeconds = 3;

    private static void ClearFollow(SimUav uav)
    {
        uav.FollowCenter = null;
        uav.FollowState = null;
        uav.FollowTrackId = null;
        uav.FollowLabel = null;
        uav.FollowForwarded = null;
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
        PayloadLockedOn = uav.PayloadLockedOn,
        PayloadZoom = Math.Round(uav.PayloadZoom, 2),
        PayloadHfovDeg = Math.Round(uav.PayloadHfovDeg, 3)
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

    /// <summary>
    /// One entry per object found, at its latest position: a moving one's updates (same mission and
    /// track id) are folded into it, not drawn as a new circle each time. In the order objects were
    /// first found. Call under <see cref="_lock"/>.
    /// </summary>
    private List<SimDetectionView> DetectionsForPage()
    {
        var byObject = new Dictionary<string, (int Order, DetectionReport Latest, int Count)>(StringComparer.Ordinal);
        var order = 0;
        foreach (var d in _detections)
        {
            var key = d.TrackId is { Length: > 0 } track ? $"{d.TailNumber}\n{d.MissionId}\n{track}" : $"#{order}";
            byObject[key] = byObject.TryGetValue(key, out var seen) ? (seen.Order, d, seen.Count + 1) : (order, d, 1);
            order++;
        }
        return byObject.Values.OrderBy(v => v.Order).TakeLast(50)
            .Select(v => new SimDetectionView(v.Latest.TailNumber, v.Latest.Prompt, v.Latest.Label, v.Latest.Confidence, v.Latest.Lat, v.Latest.Lng,
                v.Latest.DetectedAtUtc, v.Latest.TrackId, v.Latest.MissionId, v.Count))
            .ToList();
    }

    public SimFleetView View()
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
                    new CameraModel(PayloadCamera(u, u.LastFrameSeq, DateTime.UtcNow)).FootprintLngLat(),
                    u.LastFrameSeq,
                    u.Trail.Select(p => new[] { p.Lng, p.Lat }).ToList(),
                    Math.Round(u.PayloadZoom, 2), Math.Round(u.PayloadHfovDeg, 3), Surveying(u), LookAt(u),
                    FollowNow(u) is { } f ? [f.Lng, f.Lat] : null, u.FollowState, u.FollowLabel, u.FollowTrackId)).ToList(),
                DetectionsForPage());
        }
    }
}

/// <summary>A UAV for the page. The payload fields drive the 3D camera view: <see cref="Surveying"/>
/// means straight down (survey frames); <see cref="LookAt"/> ([lng, lat]) means locked on a point;
/// otherwise the gimbal's rest position, forward and down.</summary>
public sealed record SimUavView(
    string TailNumber, double Lat, double Lng, double HeadingDeg, int SpeedKts, int AltitudeFt, string Mode,
    double[]? Destination, List<double[]> Route, int? WaypointIndex, string? MissionId, string? ZoneName,
    string? SearchPrompt, bool Looking, List<double[]> Footprint, long LastFrameSeq, List<double[]> Trail,
    double PayloadZoom = 1, double PayloadHfovDeg = 40, bool Surveying = false, double[]? LookAt = null,
    double[]? Target = null, string? TargetState = null, string? TargetLabel = null, string? TargetTrackId = null);

/// <summary>One object found, where it was last seen; <see cref="Updates"/> is how many reports
/// (the first plus position updates of a moving one) it stands for.</summary>
public sealed record SimDetectionView(string TailNumber, string Prompt, string Label, double Confidence, double Lat, double Lng, DateTime DetectedAtUtc,
    string? TrackId = null, string? MissionId = null, int Updates = 1);

public sealed record SimFleetView(List<SimUavView> Uavs, List<SimDetectionView> Detections);

using UavOps.Agent.Mission;
using UavOps.Simulator.Camera;
using UavOps.Simulator.Imagery;

namespace UavOps.Simulator.World;

/// <summary>
/// Everything on the ground that can move, at any simulated moment: background
/// <see cref="Traffic"/>, and the scenario objects - parked ones where they were placed, driving
/// ones (<see cref="ScenarioObject.SpeedKmh"/> above 0) back and forth along the road through where
/// they were placed. Both the server's camera (the frames the onboard detector searches) and the
/// page's views read it, so what the operator watches and what the detector sees agree.
/// </summary>
public sealed class GroundWorld
{
    /// <summary>How close to a road a driving object must be placed.</summary>
    public const double ObjectRoadSearchMeters = 150;

    private readonly ScenarioStore _scenario;
    private readonly RoadNetwork? _roads;
    private readonly ImageryLayer? _imagery;
    private readonly bool _trafficOverPhotos;
    private readonly object _lock = new();
    private readonly Dictionary<string, (ScenarioObject Source, MovingVehicle? Drive)> _drives = [];

    public GroundWorld(ScenarioStore scenario, SimClock clock, GeoProjection projection, RoadNetwork? roads, Traffic? traffic,
        ImageryLayer? imagery = null, bool trafficOverPhotos = false)
    {
        _scenario = scenario;
        Clock = clock;
        Projection = projection;
        _roads = roads;
        Traffic = traffic;
        _imagery = imagery;
        _trafficOverPhotos = trafficOverPhotos;
    }

    public SimClock Clock { get; }
    public GeoProjection Projection { get; }
    public Traffic? Traffic { get; }

    /// <summary>The scenario objects as they are at <paramref name="simTime"/>.</summary>
    public IReadOnlyList<ScenarioObject> ObjectsAt(double simTime) =>
        _scenario.All().Select(o => Place(o, simTime)).ToList();

    public IReadOnlyList<ScenarioObject> ObjectsNow() => ObjectsAt(Clock.Now);

    /// <summary>Background traffic within <paramref name="radius"/> meters of a point at
    /// <paramref name="simTime"/>. Not over the real aerial photos unless configured: their own
    /// vehicles are the traffic there, and a drawn red car driving through a zone would be one more
    /// thing a "red car" search finds.</summary>
    public IEnumerable<(MovingVehicle Vehicle, GeoPoint Position, double HeadingDeg)> TrafficNear(GeoPoint center, double radius, double simTime)
    {
        if (Traffic is null)
            yield break;
        foreach (var (vehicle, pose) in Traffic.Near(Projection.ToLocal(center), radius, simTime))
        {
            var position = Projection.ToGeo(pose.Position);
            if (!_trafficOverPhotos && _imagery?.Covers(position) == true)
                continue;
            yield return (vehicle, position, pose.HeadingDeg);
        }
    }

    private ScenarioObject Place(ScenarioObject obj, double simTime)
    {
        if (obj.SpeedKmh <= 0 || DriveOf(obj) is not { } drive)
            return obj;
        var pose = drive.At(simTime);
        var at = Projection.ToGeo(pose.Position);
        return obj with { Lat = at.Lat, Lng = at.Lng, HeadingDeg = (pose.HeadingDeg + 360) % 360 };
    }

    /// <summary>The drive of a moving object: the road nearest where it was placed,
    /// <see cref="ScenarioObject.DriveMeters"/> each way, starting (at sim time 0) where it was
    /// placed. Null (it stays parked) if no road is near.</summary>
    private MovingVehicle? DriveOf(ScenarioObject obj)
    {
        lock (_lock)
        {
            if (_drives.TryGetValue(obj.Id, out var cached) && cached.Source == obj)
                return cached.Drive;
            var drive = BuildDrive(obj);
            _drives[obj.Id] = (obj, drive);
            return drive;
        }
    }

    private MovingVehicle? BuildDrive(ScenarioObject obj)
    {
        if (_roads is null)
            return null;
        var placed = Projection.ToLocal(new GeoPoint(obj.Lat, obj.Lng));
        if (_roads.Nearest(placed, ObjectRoadSearchMeters, RoadNetwork.VehicleClasses) is not { } nearest)
            return null;
        var edge = _roads.Edges[nearest.Edge];
        // Stable per object, so it drives the same way every run.
        var rng = new Random((int)obj.Id.Aggregate(17u, (h, c) => unchecked(h * 31 + c)));
        var forward = _roads.Drive(edge.A, nearest.Edge, obj.DriveMeters, rng, RoadNetwork.VehicleClasses);   // A, B, ...
        var backward = _roads.Drive(edge.B, nearest.Edge, obj.DriveMeters, rng, RoadNetwork.VehicleClasses);  // B, A, ...
        backward.Reverse();                                                                                    // ..., A, B
        var points = backward.Concat(forward.Skip(2)).ToList();
        if (points.Count < 2)
            return null;
        var path = new DrivePath(points);
        var kind = Enum.TryParse<VehicleKind>(obj.Kind, ignoreCase: true, out var k) ? k : VehicleKind.Car;
        // A track is one lane; on a road it keeps right.
        var lane = edge.Class == "track" ? 0 : 1.8;
        return new MovingVehicle(obj.Id, kind, obj.Color, path, obj.SpeedKmh / 3.6, path.DistanceAlong(nearest.Point),
            StartForward: rng.NextDouble() < 0.5, lane);
    }
}

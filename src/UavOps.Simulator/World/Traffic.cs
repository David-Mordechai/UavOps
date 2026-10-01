using UavOps.Agent.Mission;
using UavOps.Simulator.Camera;

namespace UavOps.Simulator.World;

/// <summary>A road drive as a polyline in local meters, measured along its length.</summary>
public sealed class DrivePath
{
    private readonly Vec2[] _points;
    private readonly double[] _along;

    public DrivePath(IReadOnlyList<Vec2> points)
    {
        if (points.Count < 2)
            throw new ArgumentException("A path needs at least two points.", nameof(points));
        _points = [.. points];
        _along = new double[_points.Length];
        for (var i = 1; i < _points.Length; i++)
            _along[i] = _along[i - 1] + Vec2.Distance(_points[i - 1], _points[i]);
        Length = _along[^1];
        Min = new Vec2(_points.Min(p => p.X), _points.Min(p => p.Y));
        Max = new Vec2(_points.Max(p => p.X), _points.Max(p => p.Y));
    }

    public double Length { get; }
    public Vec2 Min { get; }
    public Vec2 Max { get; }
    public IReadOnlyList<Vec2> Points => _points;

    /// <summary>The point <paramref name="s"/> meters along, and the direction of the path there.</summary>
    public (Vec2 Point, Vec2 Direction) At(double s)
    {
        s = Math.Clamp(s, 0, Length);
        var i = Array.BinarySearch(_along, s);
        if (i < 0)
            i = ~i;
        i = Math.Clamp(i, 1, _points.Length - 1);
        var a = _points[i - 1];
        var b = _points[i];
        var span = _along[i] - _along[i - 1];
        var t = span < 1e-9 ? 0 : (s - _along[i - 1]) / span;
        var dir = b - a;
        var len = dir.Length;
        return (a + dir * t, len < 1e-9 ? new Vec2(0, 1) : dir * (1 / len));
    }

    /// <summary>How far along the path the point nearest to <paramref name="p"/> is.</summary>
    public double DistanceAlong(Vec2 p)
    {
        var best = 0.0;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < _points.Length; i++)
        {
            var a = _points[i - 1];
            var ab = _points[i] - a;
            var len2 = ab.X * ab.X + ab.Y * ab.Y;
            var t = len2 < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
            var d = Vec2.Distance(p, a + ab * t);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = _along[i - 1] + t * Math.Sqrt(len2);
            }
        }
        return best;
    }
}

/// <summary>Where a vehicle is at a moment: local meters, and compass heading (0 = north).</summary>
public readonly record struct VehiclePose(Vec2 Position, double HeadingDeg);

/// <summary>
/// A vehicle driving <see cref="Path"/> back and forth (turning at the ends) at a steady speed, in
/// the right-hand lane: where it is is a function of sim time alone, so any moment can be drawn
/// again and nothing needs storing per tick.
/// </summary>
public sealed record MovingVehicle(string Id, VehicleKind Kind, string Color, DrivePath Path, double SpeedMps, double StartAlong,
    bool StartForward, double LaneOffsetMeters)
{
    public string SpriteKey => $"{Kind.ToString().ToLowerInvariant()}-{Color}";

    public VehiclePose At(double simTime)
    {
        var length = Path.Length;
        // Distance driven since t = 0, counted from the start of a forward leg.
        var travelled = (StartForward ? StartAlong : 2 * length - StartAlong) + SpeedMps * Math.Max(simTime, 0);
        var m = travelled % (2 * length);
        var forward = m <= length;
        var s = forward ? m : 2 * length - m;
        var (point, direction) = Path.At(s);
        if (!forward)
            direction = direction * -1;
        var right = new Vec2(direction.Y, -direction.X);
        return new VehiclePose(point + right * LaneOffsetMeters, Math.Atan2(direction.X, direction.Y) * 180 / Math.PI);
    }
}

/// <summary>
/// Background traffic on the road network: vehicles driving the main roads and streets, as many per
/// kilometre as the road's class and <see cref="SimOptions.TrafficDensity"/> give, each on a drive
/// of 1-5 km picked from a fixed seed (the same traffic every run). Never a white, silver or beige
/// van (<see cref="VehicleSprites.Background"/>).
/// </summary>
public sealed class Traffic
{
    private const double CellMeters = 1000;
    private readonly List<MovingVehicle> _vehicles = [];
    private readonly Dictionary<(int, int), List<MovingVehicle>> _cells = [];

    public Traffic(RoadNetwork roads, double density, int seed = 20260929)
    {
        var rng = new Random(seed);
        var id = 0;
        density = Math.Clamp(density, 0, 1);
        foreach (var (edge, index) in roads.Edges.Select((e, i) => (e, i)))
        {
            if (!RoadNetwork.TrafficClasses.Contains(edge.Class))
                continue;
            // Vehicles per km at density 1 (a busy town), per class.
            var perKm = edge.Class switch
            {
                "motorway" => 30.0,
                "trunk" => 26,
                "primary" => 22,
                "secondary" => 16,
                "tertiary" => 10,
                "minor" => 5,
                _ => 1.5
            } * density;
            var expected = perKm * edge.Length / 1000;
            var count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
            for (var i = 0; i < count; i++)
            {
                var fromA = rng.NextDouble() < 0.5;
                var points = roads.Drive(fromA ? edge.A : edge.B, index, 1000 + rng.NextDouble() * 4000, rng, RoadNetwork.TrafficClasses);
                if (points.Count < 2)
                    continue;
                var path = new DrivePath(points);
                if (path.Length < 100)
                    continue;
                var (kind, color) = VehicleSprites.Background(rng);
                var kmh = SpeedKmh(edge.Class) * (0.85 + rng.NextDouble() * 0.3);
                var lane = edge.Class is "motorway" or "trunk" or "primary" ? 3.2 : 1.8;
                Add(new MovingVehicle($"trf-{++id}", kind, color, path, kmh / 3.6, rng.NextDouble() * path.Length, rng.NextDouble() < 0.5, lane));
            }
        }
    }

    public IReadOnlyList<MovingVehicle> All => _vehicles;

    public static double SpeedKmh(string roadClass) => roadClass switch
    {
        "motorway" => 100,
        "trunk" => 90,
        "primary" => 70,
        "secondary" => 60,
        "tertiary" => 50,
        "minor" => 30,
        "track" => 25,
        _ => 15
    };

    private void Add(MovingVehicle vehicle)
    {
        _vehicles.Add(vehicle);
        for (var x = Cell(vehicle.Path.Min.X); x <= Cell(vehicle.Path.Max.X); x++)
        for (var y = Cell(vehicle.Path.Min.Y); y <= Cell(vehicle.Path.Max.Y); y++)
        {
            if (!_cells.TryGetValue((x, y), out var list))
                _cells[(x, y)] = list = [];
            list.Add(vehicle);
        }
    }

    /// <summary>Every vehicle within <paramref name="radius"/> meters of <paramref name="center"/> at
    /// <paramref name="simTime"/>.</summary>
    public IEnumerable<(MovingVehicle Vehicle, VehiclePose Pose)> Near(Vec2 center, double radius, double simTime)
    {
        var seen = new HashSet<string>();
        for (var x = Cell(center.X - radius); x <= Cell(center.X + radius); x++)
        for (var y = Cell(center.Y - radius); y <= Cell(center.Y + radius); y++)
        {
            if (!_cells.TryGetValue((x, y), out var list))
                continue;
            foreach (var v in list)
            {
                if (!seen.Add(v.Id))
                    continue;
                var pose = v.At(simTime);
                if (Vec2.Distance(pose.Position, center) <= radius)
                    yield return (v, pose);
            }
        }
    }

    private static int Cell(double meters) => (int)Math.Floor(meters / CellMeters);
}

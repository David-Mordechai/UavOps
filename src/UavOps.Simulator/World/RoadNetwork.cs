using UavOps.Agent.Mission;
using UavOps.Simulator.Map;

namespace UavOps.Simulator.World;

/// <summary>A straight piece of road between two graph nodes, in local meters.</summary>
public sealed record RoadEdge(int A, int B, string Class, double Length);

/// <summary>
/// The roads cars can drive, as a graph in the ground's local frame: read once from the offline OSM
/// map (<c>transportation</c> layer, the same tiles the camera draws), for the areas that get
/// traffic. Each line is clipped to its own tile (vector tiles repeat a margin of their
/// neighbours), and vertices closer than <see cref="SnapMeters"/> become one node - junctions share
/// a vertex in OSM, and a road crossing a tile edge is cut at the same point from both sides.
/// </summary>
public sealed class RoadNetwork
{
    public const double SnapMeters = 1.5;

    /// <summary>Roads background traffic drives. Tracks are left to scenario vehicles.</summary>
    public static readonly HashSet<string> TrafficClasses = ["motorway", "trunk", "primary", "secondary", "tertiary", "minor", "service"];

    /// <summary>Roads a scenario vehicle may drive: the traffic roads plus tracks (the Yatir forest
    /// road where the zone's targets are).</summary>
    public static readonly HashSet<string> VehicleClasses = [.. TrafficClasses, "track"];

    private readonly List<Vec2> _nodes = [];
    private readonly List<RoadEdge> _edges = [];
    private readonly List<List<int>> _adjacent = [];
    private readonly Dictionary<(long, long), List<int>> _cells = [];

    public RoadNetwork(GeoProjection projection)
    {
        Projection = projection;
    }

    /// <summary>The local frame the network is in (the ground's, so the camera can use it directly).</summary>
    public GeoProjection Projection { get; }

    public IReadOnlyList<Vec2> Nodes => _nodes;
    public IReadOnlyList<RoadEdge> Edges => _edges;
    public IReadOnlyList<int> EdgesAt(int node) => _adjacent[node];

    /// <summary>Reads every road of <see cref="VehicleClasses"/> in the tiles covering the areas.</summary>
    public static RoadNetwork Load(PmTilesReader tiles, GeoProjection projection, IEnumerable<(GeoPoint Center, double RadiusMeters)> areas)
    {
        var network = new RoadNetwork(projection);
        var zoom = tiles.MaxZoom;
        var wanted = new HashSet<(int X, int Y)>();
        foreach (var (center, radius) in areas)
        {
            var local = new GeoProjection(center);
            var sw = local.ToGeo(new Vec2(-radius, -radius));
            var ne = local.ToGeo(new Vec2(radius, radius));
            var (x0, y0) = TileMath.TileAt(ne.Lat, sw.Lng, zoom);
            var (x1, y1) = TileMath.TileAt(sw.Lat, ne.Lng, zoom);
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                wanted.Add((x, y));
        }

        var layerNames = new HashSet<string> { "transportation" };
        foreach (var (tx, ty) in wanted)
        {
            if (tiles.GetTile(zoom, tx, ty) is not { } bytes)
                continue;
            if (!VectorTileDecoder.Decode(bytes, layerNames).TryGetValue("transportation", out var layer))
                continue;
            foreach (var feature in layer.Features)
            {
                if (feature.Type != GeometryType.LineString || feature.Get("class") is not { } cls || !VehicleClasses.Contains(cls))
                    continue;
                foreach (var part in feature.Parts)
                foreach (var piece in ClipToTile(part, layer.Extent))
                {
                    var points = piece.Select(p =>
                    {
                        var (lat, lng) = TileMath.ToGeo(zoom, tx, ty, p.X, p.Y, layer.Extent);
                        return projection.ToLocal(new GeoPoint(lat, lng));
                    }).ToList();
                    network.AddLine(cls, points);
                }
            }
        }
        return network;
    }

    /// <summary>Adds a road line (local meters) as edges between snapped nodes.</summary>
    public void AddLine(string roadClass, IReadOnlyList<Vec2> points)
    {
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = NodeAt(points[i]);
            var b = NodeAt(points[i + 1]);
            if (a == b)
                continue;
            // The same piece twice (a tile's margin that survived clipping) adds nothing.
            if (_adjacent[a].Any(e => _edges[e].A == b || _edges[e].B == b))
                continue;
            var edge = _edges.Count;
            _edges.Add(new RoadEdge(a, b, roadClass, Vec2.Distance(_nodes[a], _nodes[b])));
            _adjacent[a].Add(edge);
            _adjacent[b].Add(edge);
        }
    }

    private int NodeAt(Vec2 p)
    {
        var cx = (long)Math.Floor(p.X / SnapMeters);
        var cy = (long)Math.Floor(p.Y / SnapMeters);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (!_cells.TryGetValue((cx + dx, cy + dy), out var ids))
                continue;
            foreach (var id in ids)
                if (Vec2.Distance(_nodes[id], p) <= SnapMeters)
                    return id;
        }
        var node = _nodes.Count;
        _nodes.Add(p);
        _adjacent.Add([]);
        if (!_cells.TryGetValue((cx, cy), out var cell))
            _cells[(cx, cy)] = cell = [];
        cell.Add(node);
        return node;
    }

    /// <summary>The point of a road (of <paramref name="classes"/>) nearest to <paramref name="p"/>,
    /// within <paramref name="maxDistance"/>.</summary>
    public (int Edge, double Along, Vec2 Point)? Nearest(Vec2 p, double maxDistance, IReadOnlySet<string> classes)
    {
        (int, double, Vec2)? best = null;
        var bestDistance = maxDistance;
        for (var i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            if (!classes.Contains(e.Class))
                continue;
            var a = _nodes[e.A];
            var b = _nodes[e.B];
            var ab = b - a;
            var t = e.Length < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / (e.Length * e.Length), 0, 1);
            var q = a + ab * t;
            var d = Vec2.Distance(p, q);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = (i, t * e.Length, q);
            }
        }
        return best;
    }

    /// <summary>
    /// A drive of about <paramref name="length"/> meters leaving <paramref name="fromNode"/> along
    /// <paramref name="firstEdge"/>: at each junction it keeps going, mostly straight on (a turn is
    /// less likely the sharper it is), never straight back, until the length is reached or the road
    /// ends. Returns the points passed, starting at <paramref name="fromNode"/>.
    /// </summary>
    public List<Vec2> Drive(int fromNode, int firstEdge, double length, Random rng, IReadOnlySet<string> classes)
    {
        var points = new List<Vec2> { _nodes[fromNode] };
        var node = fromNode;
        var edge = firstEdge;
        var travelled = 0.0;
        var visited = new HashSet<int>();
        while (travelled < length && visited.Add(edge))
        {
            var e = _edges[edge];
            var next = e.A == node ? e.B : e.A;
            points.Add(_nodes[next]);
            travelled += e.Length;
            var heading = _nodes[next] - _nodes[node];
            node = next;

            var options = _adjacent[node].Where(x => x != edge && classes.Contains(_edges[x].Class) && !visited.Contains(x)).ToList();
            if (options.Count == 0)
                break;
            var weights = options.Select(x =>
            {
                var o = _edges[x];
                var dir = _nodes[o.A == node ? o.B : o.A] - _nodes[node];
                var cos = (heading.X * dir.X + heading.Y * dir.Y) / Math.Max(heading.Length * dir.Length, 1e-9);
                return Math.Pow((cos + 1.2) / 2.2, 4); // straight on ≈ 1, a right angle ≈ 0.09
            }).ToList();
            var roll = rng.NextDouble() * weights.Sum();
            edge = options[^1];
            for (var i = 0; i < options.Count; i++)
            {
                if ((roll -= weights[i]) <= 0)
                {
                    edge = options[i];
                    break;
                }
            }
        }
        return points;
    }

    /// <summary>A polyline in tile coordinates cut to the tile itself (0..extent on both axes);
    /// crossing out and back in gives several pieces.</summary>
    public static IEnumerable<List<(double X, double Y)>> ClipToTile(IReadOnlyList<(int X, int Y)> line, int extent)
    {
        var piece = new List<(double X, double Y)>();
        for (var i = 0; i + 1 < line.Count; i++)
        {
            if (!ClipSegment(line[i].X, line[i].Y, line[i + 1].X, line[i + 1].Y, extent, out var a, out var b))
            {
                if (piece.Count >= 2)
                    yield return piece;
                piece = [];
                continue;
            }
            if (piece.Count > 0 && (Math.Abs(piece[^1].X - a.X) > 1e-6 || Math.Abs(piece[^1].Y - a.Y) > 1e-6))
            {
                if (piece.Count >= 2)
                    yield return piece;
                piece = [];
            }
            if (piece.Count == 0)
                piece.Add(a);
            piece.Add(b);
        }
        if (piece.Count >= 2)
            yield return piece;
    }

    /// <summary>Liang-Barsky against [0, extent]².</summary>
    private static bool ClipSegment(double x0, double y0, double x1, double y1, int extent,
        out (double X, double Y) a, out (double X, double Y) b)
    {
        double t0 = 0, t1 = 1;
        var dx = x1 - x0;
        var dy = y1 - y0;
        foreach (var (p, q) in new[] { (-dx, x0), (dx, extent - x0), (-dy, y0), (dy, extent - y0) })
        {
            if (Math.Abs(p) < 1e-12)
            {
                if (q < 0)
                {
                    a = b = default;
                    return false;
                }
                continue;
            }
            var r = q / p;
            if (p < 0)
                t0 = Math.Max(t0, r);
            else
                t1 = Math.Min(t1, r);
            if (t0 > t1)
            {
                a = b = default;
                return false;
            }
        }
        a = (x0 + t0 * dx, y0 + t0 * dy);
        b = (x0 + t1 * dx, y0 + t1 * dy);
        return t1 - t0 > 1e-9;
    }
}

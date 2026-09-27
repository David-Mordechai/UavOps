namespace UavOps.Agent.Mission;

public sealed record SearchPlanParameters(
    int AltitudeFt,
    double CameraHorizontalFovDeg,
    double SideOverlap,
    int MaxWaypoints,
    double SpeedKts)
{
    public const double MetersPerFoot = 0.3048;
    public const double MetersPerSecondPerKnot = 0.514444;

    /// <summary>Width of ground the camera sees across track, flying level.</summary>
    public double FootprintWidthMeters =>
        2 * AltitudeFt * MetersPerFoot * Math.Tan(CameraHorizontalFovDeg * Math.PI / 360);

    public double LaneSpacingMeters => FootprintWidthMeters * (1 - SideOverlap);
}

/// <summary>A planned search route: waypoints to fly in order, all at <see cref="AltitudeFt"/>.</summary>
public sealed record SearchRoute(
    string RouteId,
    string TailNumber,
    string ZoneName,
    IReadOnlyList<GeoPoint> Waypoints,
    int AltitudeFt,
    double LengthMeters,
    TimeSpan EstimatedDuration,
    int LaneCount,
    double LaneSpacingMeters);

public sealed class SearchPlanException(string message) : Exception(message);

/// <summary>
/// Plans a lawnmower (boustrophedon) sweep over a polygon: parallel lanes one camera footprint
/// apart (less the side overlap), flown back and forth.
/// </summary>
public static class SearchRoutePlanner
{
    public static SearchRoute Plan(
        AoiZone zone,
        string tailNumber,
        GeoPoint? uavPosition,
        SearchPlanParameters parameters)
    {
        if (zone.Vertices.Count < 3)
            throw new SearchPlanException($"Zone '{zone.Name}' has {zone.Vertices.Count} vertices; a search area needs at least 3.");
        if (parameters.AltitudeFt <= 0)
            throw new SearchPlanException("Search altitude must be above 0 ft.");
        if (parameters.SideOverlap is < 0 or >= 1)
            throw new SearchPlanException("Side overlap must be at least 0 and less than 1.");

        var projection = GeoProjection.Around(zone.Vertices);
        var ring = zone.Vertices.Select(projection.ToLocal).ToList();
        if (Polygon.Area(ring) < 1)
            throw new SearchPlanException($"Zone '{zone.Name}' has no area.");

        // Rotate so lanes run along the x axis in the direction that needs the fewest of them.
        var angle = MinimumWidthAngle(ring);
        var rotated = ring.Select(p => Rotate(p, -angle)).ToList();
        var spacing = parameters.LaneSpacingMeters;
        var lanes = LaneOffsets(rotated.Min(p => p.Y), rotated.Max(p => p.Y), spacing)
            .Select((y, index) => Scanline(rotated, y).Select(s => s with { Lane = index }).ToList())
            .ToList();

        var uavLocal = uavPosition is { } position ? Rotate(projection.ToLocal(position), -angle) : (Vec2?)null;
        var best = Variants(lanes)
            .Select(path => (path, cost: PathLength(path) + (uavLocal is { } u ? Vec2.Distance(u, path[0]) : 0)))
            .MinBy(v => v.cost)
            .path;

        if (best.Count > parameters.MaxWaypoints)
            throw new SearchPlanException(
                $"Zone '{zone.Name}' needs {best.Count} waypoints at {parameters.AltitudeFt} ft, over the limit of " +
                $"{parameters.MaxWaypoints}. Search from a higher altitude, or split the zone.");

        var length = PathLength(best);
        var speed = Math.Max(parameters.SpeedKts, 1) * SearchPlanParameters.MetersPerSecondPerKnot;
        return new SearchRoute(
            RouteId: $"{zone.Name}-{tailNumber}-{Guid.NewGuid().ToString("N")[..6]}",
            TailNumber: tailNumber,
            ZoneName: zone.Name,
            Waypoints: best.Select(p => projection.ToGeo(Rotate(p, angle))).ToList(),
            AltitudeFt: parameters.AltitudeFt,
            LengthMeters: Math.Round(length),
            EstimatedDuration: TimeSpan.FromSeconds(Math.Round(length / speed)),
            LaneCount: lanes.Count(l => l.Count > 0),
            LaneSpacingMeters: Math.Round(spacing, 1));
    }

    /// <summary>Lane positions across the zone. A zone no wider than one lane gets a single
    /// centreline pass; otherwise lanes are spaced evenly and centred, so the outer ones sit at
    /// most half a spacing in from the edges.</summary>
    internal static List<double> LaneOffsets(double minY, double maxY, double spacing)
    {
        var height = maxY - minY;
        if (height <= spacing)
            return [minY + height / 2];

        var count = (int)Math.Ceiling(height / spacing);
        var first = minY + (height - (count - 1) * spacing) / 2;
        return Enumerable.Range(0, count).Select(i => first + i * spacing).ToList();
    }

    internal readonly record struct Segment(double MinX, double MaxX, double Y, int Lane);

    /// <summary>Where the horizontal line <paramref name="y"/> is inside the polygon: sorted edge
    /// crossings, paired. A concave polygon can give several segments per lane.</summary>
    internal static IEnumerable<Segment> Scanline(IReadOnlyList<Vec2> ring, double y)
    {
        var crossings = new List<double>();
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            // Half-open, so a lane passing exactly through a vertex counts it once.
            if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
                crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
        }
        crossings.Sort();
        for (var i = 0; i + 1 < crossings.Count; i += 2)
        {
            if (crossings[i + 1] - crossings[i] > 1e-6)
                yield return new Segment(crossings[i], crossings[i + 1], y, 0);
        }
    }

    /// <summary>The 4 ways to start: bottom or top lane, left or right end.</summary>
    private static IEnumerable<List<Vec2>> Variants(List<List<Segment>> lanes)
    {
        var nonEmpty = lanes.Where(l => l.Count > 0).ToList();
        if (nonEmpty.Count == 0)
            yield break;

        foreach (var fromBottom in new[] { true, false })
        {
            var firstLane = fromBottom ? nonEmpty[0] : nonEmpty[^1];
            foreach (var fromLeft in new[] { true, false })
            {
                var start = fromLeft ? firstLane.MinBy(s => s.MinX) : firstLane.MaxBy(s => s.MaxX);
                yield return Chain(lanes, start, fromLeft, fromBottom ? 1 : -1);
            }
        }
    }

    /// <summary>
    /// Serpentine through the segments: each next segment is the one on the adjacent lane that
    /// overlaps the current one (the same sweep of the same part of the zone). When a part is
    /// finished, the nearest leftover segment starts the next.
    /// </summary>
    private static List<Vec2> Chain(List<List<Segment>> lanes, Segment start, bool enterFromLeft, int direction)
    {
        var remaining = lanes.SelectMany(l => l).ToHashSet();
        var path = new List<Vec2>();
        var current = start;
        var position = enterFromLeft ? new Vec2(start.MinX, start.Y) : new Vec2(start.MaxX, start.Y);

        while (true)
        {
            remaining.Remove(current);
            var left = new Vec2(current.MinX, current.Y);
            var right = new Vec2(current.MaxX, current.Y);
            var (entry, exit) = Vec2.Distance(position, left) <= Vec2.Distance(position, right) ? (left, right) : (right, left);
            path.Add(entry);
            path.Add(exit);
            position = exit;

            if (remaining.Count == 0)
                return path;

            var from = current;
            var next = NextOnLane(remaining, from, from.Lane + direction, position);
            if (next is null)
            {
                next = NextOnLane(remaining, from, from.Lane - direction, position);
                if (next is not null)
                    direction = -direction;
            }
            if (next is null)
            {
                var exitPoint = position;
                next = remaining.MinBy(s => Math.Min(
                    Vec2.Distance(exitPoint, new Vec2(s.MinX, s.Y)),
                    Vec2.Distance(exitPoint, new Vec2(s.MaxX, s.Y))));
                direction = Math.Sign(next.Value.Lane - from.Lane) is var sign and not 0 ? sign : direction;
            }
            current = next.Value;
        }
    }

    private static Segment? NextOnLane(HashSet<Segment> remaining, Segment from, int lane, Vec2 position)
    {
        Segment? best = null;
        var bestDistance = double.MaxValue;
        foreach (var s in remaining)
        {
            if (s.Lane != lane || s.MaxX < from.MinX || s.MinX > from.MaxX)
                continue;
            var distance = Math.Min(Math.Abs(s.MinX - position.X), Math.Abs(s.MaxX - position.X));
            if (distance < bestDistance)
            {
                best = s;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>Angle of the lane direction: parallel to the convex hull edge whose opposite side
    /// is nearest (rotating calipers), which is the minimum-width direction.</summary>
    internal static double MinimumWidthAngle(IReadOnlyList<Vec2> ring)
    {
        var hull = Polygon.ConvexHull(ring);
        var bestAngle = 0.0;
        var bestWidth = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var edge = hull[(i + 1) % hull.Count] - a;
            var length = edge.Length;
            if (length < 1e-9)
                continue;
            var width = hull.Max(p => Math.Abs(Vec2.Cross(edge, p - a)) / length);
            // Small tolerance so ties (e.g. a rectangle's two long sides) resolve the same way every time.
            if (width < bestWidth - 1e-6)
            {
                bestWidth = width;
                bestAngle = Math.Atan2(edge.Y, edge.X);
            }
        }
        return bestAngle;
    }

    private static Vec2 Rotate(Vec2 p, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new Vec2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
    }

    private static double PathLength(IReadOnlyList<Vec2> path)
    {
        double length = 0;
        for (var i = 1; i < path.Count; i++)
            length += Vec2.Distance(path[i - 1], path[i]);
        return length;
    }
}

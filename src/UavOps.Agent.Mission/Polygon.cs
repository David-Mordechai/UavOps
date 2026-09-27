namespace UavOps.Agent.Mission;

/// <summary>Planar polygon helpers over a <see cref="GeoProjection"/>'s local frame.</summary>
public static class Polygon
{
    public static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        double sum = 0;
        for (var i = 0; i < ring.Count; i++)
            sum += Vec2.Cross(ring[i], ring[(i + 1) % ring.Count]);
        return sum / 2;
    }

    public static double Area(IReadOnlyList<Vec2> ring) => Math.Abs(SignedArea(ring));

    public static Vec2 Centroid(IReadOnlyList<Vec2> ring)
    {
        var a = SignedArea(ring);
        if (Math.Abs(a) < 1e-9)
            return new Vec2(ring.Average(p => p.X), ring.Average(p => p.Y));

        double cx = 0, cy = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            var cross = Vec2.Cross(p, q);
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        return new Vec2(cx / (6 * a), cy / (6 * a));
    }

    /// <summary>Even-odd rule; points exactly on an edge may go either way.</summary>
    public static bool Contains(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static double DistanceToBoundary(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        var best = double.MaxValue;
        for (var i = 0; i < ring.Count; i++)
            best = Math.Min(best, DistanceToSegment(point, ring[i], ring[(i + 1) % ring.Count]));
        return best;
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var lengthSq = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSq < 1e-12)
            return Vec2.Distance(p, a);
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSq, 0, 1);
        return Vec2.Distance(p, a + ab * t);
    }

    /// <summary>Andrew's monotone chain; counter-clockwise, no collinear points.</summary>
    public static List<Vec2> ConvexHull(IEnumerable<Vec2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3)
            return sorted;

        var hull = new List<Vec2>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Vec2.Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }
}

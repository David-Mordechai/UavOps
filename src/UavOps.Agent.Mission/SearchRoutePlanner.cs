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

/// <summary>One UAV of a team search: where it is now, the altitude it searches at (its own - a
/// search never changes a UAV's altitude) and its speed.</summary>
public sealed record SearchTeamMember(string TailNumber, GeoPoint? Position, int AltitudeFt, double SpeedKts);

/// <summary>A zone split between UAVs: one route per UAV over its own band of lanes. Every route's id
/// ends in <see cref="TeamId"/>. <see cref="UnusedTails"/> are members left out because the zone
/// has fewer lanes than the team has UAVs.</summary>
public sealed record TeamSearchPlan(
    string TeamId,
    string ZoneName,
    IReadOnlyList<SearchRoute> Routes,
    IReadOnlyList<string> UnusedTails);

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
        if (parameters.SideOverlap is < 0 or >= 1)
            throw new SearchPlanException("Side overlap must be at least 0 and less than 1.");

        // One UAV is a team of one: the band is the whole zone.
        var member = new SearchTeamMember(tailNumber, uavPosition, parameters.AltitudeFt, parameters.SpeedKts);
        return PlanTeam(zone, [member], parameters.LaneSpacingMeters, parameters.MaxWaypoints).Routes[0];
    }

    /// <summary>
    /// Splits one sweep of the zone between the team. The lanes are those of a single search at
    /// <paramref name="laneSpacingMeters"/>, cut into contiguous bands, one per UAV: two UAVs'
    /// bands meet one lane spacing apart, so nothing is left out and nothing is searched twice.
    /// The cuts and which UAV takes which band minimise the time until the last UAV is done
    /// (getting to its band plus flying it, at its own speed), so the nearer UAV takes the nearer
    /// band and a faster one takes more lanes. A UAV is left out when the zone is done sooner without
    /// it (e.g. it is so far away that the others finish before it could arrive), and with more UAVs
    /// than lanes the extras aren't used.
    /// </summary>
    public static TeamSearchPlan PlanTeam(
        AoiZone zone,
        IReadOnlyList<SearchTeamMember> members,
        double laneSpacingMeters,
        int maxWaypoints)
    {
        if (zone.Vertices.Count < 3)
            throw new SearchPlanException($"Zone '{zone.Name}' has {zone.Vertices.Count} vertices; a search area needs at least 3.");
        if (members.Count == 0)
            throw new SearchPlanException("A search needs at least one UAV.");
        if (members.Any(m => m.AltitudeFt <= 0))
            throw new SearchPlanException("Search altitude must be above 0 ft.");
        if (laneSpacingMeters <= 0)
            throw new SearchPlanException("Lane spacing must be above 0 m.");

        var projection = GeoProjection.Around(zone.Vertices);
        var ring = zone.Vertices.Select(projection.ToLocal).ToList();
        if (Polygon.Area(ring) < 1)
            throw new SearchPlanException($"Zone '{zone.Name}' has no area.");

        // Rotate so lanes run along the x axis in the direction that needs the fewest of them.
        var angle = MinimumWidthAngle(ring);
        var rotated = ring.Select(p => Rotate(p, -angle)).ToList();
        var lanes = LaneOffsets(rotated.Min(p => p.Y), rotated.Max(p => p.Y), laneSpacingMeters)
            .Select((y, index) => Scanline(rotated, y).Select(s => s with { Lane = index }).ToList())
            .Where(l => l.Count > 0)
            .ToList();
        if (lanes.Count == 0)
            throw new SearchPlanException($"Zone '{zone.Name}' has no area.");

        var positions = members
            .Select(m => m.Position is { } p ? Rotate(projection.ToLocal(p), -angle) : (Vec2?)null)
            .ToList();
        var (order, bands) = SplitIntoBands(lanes, members, positions, laneSpacingMeters);

        var teamId = Guid.NewGuid().ToString("N")[..6];
        var routes = new List<SearchRoute>();
        for (var b = 0; b < bands.Count; b++)
        {
            var member = members[order[b]];
            var uavLocal = positions[order[b]];
            var bandLanes = lanes.GetRange(bands[b].First, bands[b].Count);
            var best = Variants(bandLanes)
                .Select(path => (path, cost: PathLength(path) + (uavLocal is { } u ? Vec2.Distance(u, path[0]) : 0)))
                .MinBy(v => v.cost)
                .path;

            if (best.Count > maxWaypoints)
            {
                var what = members.Count == 1 ? $"Zone '{zone.Name}'" : $"{member.TailNumber}'s part of zone '{zone.Name}'";
                throw new SearchPlanException(
                    $"{what} needs {best.Count} waypoints at {member.AltitudeFt} ft, over the limit of " +
                    $"{maxWaypoints}. Search from a higher altitude, or split the zone.");
            }

            var length = PathLength(best);
            var speed = Math.Max(member.SpeedKts, 1) * SearchPlanParameters.MetersPerSecondPerKnot;
            routes.Add(new SearchRoute(
                RouteId: $"{zone.Name}-{member.TailNumber}-{teamId}",
                TailNumber: member.TailNumber,
                ZoneName: zone.Name,
                Waypoints: best.Select(p => projection.ToGeo(Rotate(p, angle))).ToList(),
                AltitudeFt: member.AltitudeFt,
                LengthMeters: Math.Round(length),
                EstimatedDuration: TimeSpan.FromSeconds(Math.Round(length / speed)),
                LaneCount: bandLanes.Count,
                LaneSpacingMeters: Math.Round(laneSpacingMeters, 1)));
        }

        var used = order.Take(bands.Count).ToHashSet();
        var unused = members.Where((_, i) => !used.Contains(i)).Select(m => m.TailNumber).ToList();
        return new TeamSearchPlan(teamId, zone.Name, routes, unused);
    }

    /// <summary>
    /// Which member flies which contiguous run of lanes (band b goes to member order[b]). Every
    /// ordering of the members over the bands is tried, and for each the cuts that minimise the
    /// latest finish (dynamic programming over the lanes); the tie-break is the total time. Fewer
    /// bands than members are tried too: a smaller team wins only when it finishes sooner (by more
    /// than a second), so every UAV the operator named is used unless it would only slow the team
    /// down - a UAV 25 minutes away given one lane of a zone the others finish in two. A band's
    /// time is estimated, not chained: its segments' length plus one spacing per lane change, plus
    /// the way from the UAV to the nearest end of its first or last lane. Teams are a handful of
    /// UAVs and zones tens of lanes, so this is cheap.
    /// </summary>
    private static (int[] Order, List<(int First, int Count)> Bands) SplitIntoBands(
        List<List<Segment>> lanes,
        IReadOnlyList<SearchTeamMember> members,
        IReadOnlyList<Vec2?> positions,
        double spacing)
    {
        var laneCount = lanes.Count;
        var laneLength = new double[laneCount + 1];
        for (var i = 0; i < laneCount; i++)
            laneLength[i + 1] = laneLength[i] + lanes[i].Sum(s => s.MaxX - s.MinX);

        double BandSeconds(int member, int first, int last)
        {
            var length = laneLength[last + 1] - laneLength[first] + (last - first) * spacing;
            if (positions[member] is { } u)
            {
                var ends = new[] { lanes[first], lanes[last] }
                    .SelectMany(l => new[] { new Vec2(l.Min(s => s.MinX), l[0].Y), new Vec2(l.Max(s => s.MaxX), l[0].Y) });
                length += ends.Min(e => Vec2.Distance(u, e));
            }
            return length / (Math.Max(members[member].SpeedKts, 1) * SearchPlanParameters.MetersPerSecondPerKnot);
        }

        (int[] Order, List<(int, int)> Bands, (double Max, double Sum) Cost)? chosen = null;
        for (var bandCount = Math.Min(members.Count, laneCount); bandCount >= 1; bandCount--)
        {
            var split = BestSplit(bandCount);
            // Largest team first: a smaller one has to finish strictly sooner to replace it.
            if (chosen is null || split.Cost.Max < chosen.Value.Cost.Max - 1)
                chosen = split;
        }
        return (chosen!.Value.Order, chosen.Value.Bands);

        (int[] Order, List<(int, int)> Bands, (double Max, double Sum) Cost) BestSplit(int bandCount)
        {
            int[]? bestOrder = null;
            List<(int, int)>? bestBands = null;
            var bestCost = (Max: double.MaxValue, Sum: double.MaxValue);
            foreach (var order in Arrangements(members.Count, bandCount))
            {
                // cost[b, end]: best (latest finish, total) with bands 0..b covering lanes 0..end.
                var cost = new (double Max, double Sum)[bandCount, laneCount];
                var cut = new int[bandCount, laneCount];
                for (var b = 0; b < bandCount; b++)
                {
                    for (var end = b; end < laneCount - (bandCount - 1 - b); end++)
                    {
                        cost[b, end] = (double.MaxValue, double.MaxValue);
                        for (var first = b == 0 ? 0 : b; first <= end; first++)
                        {
                            if (b == 0 && first != 0)
                                break;
                            var own = BandSeconds(order[b], first, end);
                            var before = b == 0 ? (Max: 0.0, Sum: 0.0) : cost[b - 1, first - 1];
                            var candidate = (Max: Math.Max(before.Max, own), Sum: before.Sum + own);
                            if (Better(candidate, cost[b, end]))
                            {
                                cost[b, end] = candidate;
                                cut[b, end] = first;
                            }
                        }
                    }
                }

                var total = cost[bandCount - 1, laneCount - 1];
                if (!Better(total, bestCost))
                    continue;
                bestCost = total;
                bestOrder = order;
                bestBands = [];
                for (int b = bandCount - 1, end = laneCount - 1; b >= 0; b--)
                {
                    var first = cut[b, end];
                    bestBands.Insert(0, (first, end - first + 1));
                    end = first - 1;
                }
            }
            return (bestOrder!, bestBands!, bestCost);
        }

        // Within a second on the latest finish counts as equal; then the lower total wins.
        static bool Better((double Max, double Sum) a, (double Max, double Sum) b) =>
            a.Max < b.Max - 1 || (Math.Abs(a.Max - b.Max) <= 1 && a.Sum < b.Sum);
    }

    /// <summary>Every ordered choice of <paramref name="k"/> of <paramref name="n"/> member indices.</summary>
    private static IEnumerable<int[]> Arrangements(int n, int k)
    {
        var chosen = new int[k];
        var used = new bool[n];
        return Next(0);

        IEnumerable<int[]> Next(int position)
        {
            if (position == k)
            {
                yield return (int[])chosen.Clone();
                yield break;
            }
            for (var i = 0; i < n; i++)
            {
                if (used[i])
                    continue;
                used[i] = true;
                chosen[position] = i;
                foreach (var arrangement in Next(position + 1))
                    yield return arrangement;
                used[i] = false;
            }
        }
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

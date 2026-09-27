using FluentAssertions;
using UavOps.Agent.Mission;

namespace UavOps.Agent.Tests.Mission;

public class SearchRoutePlannerTests
{
    // 1000 ft, 60° HFOV, 20% overlap: footprint ~352 m, lane spacing ~281.6 m.
    private static readonly SearchPlanParameters Defaults = new(1000, 60, 0.2, 300, 100);

    private static readonly GeoProjection Frame = new(new GeoPoint(31.81, 34.66));

    /// <summary>A polygon given in local meters around <see cref="Frame"/>'s origin.</summary>
    private static AoiZone Zone(string name, params (double X, double Y)[] meters) =>
        new(name, meters.Select(m => Frame.ToGeo(new Vec2(m.X, m.Y))).ToList());

    private static List<Vec2> Local(IEnumerable<GeoPoint> points) => points.Select(Frame.ToLocal).ToList();

    [Fact]
    public void LaneSpacing_FollowsAltitudeFovAndOverlap()
    {
        Defaults.FootprintWidthMeters.Should().BeApproximately(2 * 304.8 * Math.Tan(Math.PI / 6), 0.01);
        Defaults.LaneSpacingMeters.Should().BeApproximately(Defaults.FootprintWidthMeters * 0.8, 0.01);
    }

    [Fact]
    public void Rectangle_SweepsAcrossItsShortSide_WithEvenlySpacedLanes()
    {
        // 2000 m east-west, 1000 m north-south: lanes should run east-west.
        var zone = Zone("Rect", (0, 0), (2000, 0), (2000, 1000), (0, 1000));

        var route = SearchRoutePlanner.Plan(zone, "997", null, Defaults);

        var expectedLanes = (int)Math.Ceiling(1000 / Defaults.LaneSpacingMeters);
        route.LaneCount.Should().Be(expectedLanes);
        route.Waypoints.Should().HaveCount(2 * expectedLanes);

        var local = Local(route.Waypoints);
        var laneYs = local.Select(p => Math.Round(p.Y, 1)).Distinct().OrderBy(y => y).ToList();
        laneYs.Should().HaveCount(expectedLanes);
        for (var i = 1; i < laneYs.Count; i++)
            (laneYs[i] - laneYs[i - 1]).Should().BeApproximately(Defaults.LaneSpacingMeters, 0.5);
        laneYs[0].Should().BeLessThanOrEqualTo(Defaults.LaneSpacingMeters / 2 + 0.5, "the outer lane sits at most half a spacing in");
        (1000 - laneYs[^1]).Should().BeLessThanOrEqualTo(Defaults.LaneSpacingMeters / 2 + 0.5);

        // Each lane spans the full 2 km.
        local.Select(p => p.X).Min().Should().BeApproximately(0, 0.5);
        local.Select(p => p.X).Max().Should().BeApproximately(2000, 0.5);
    }

    [Fact]
    public void Rectangle_IsFlownSerpentine()
    {
        var zone = Zone("Rect", (0, 0), (2000, 0), (2000, 1000), (0, 1000));

        var local = Local(SearchRoutePlanner.Plan(zone, "997", null, Defaults).Waypoints);

        // Pairs of waypoints are lanes; consecutive lanes are flown in opposite directions and
        // each starts at the end the previous one finished.
        for (var i = 2; i < local.Count; i += 2)
        {
            Math.Sign(local[i + 1].X - local[i].X).Should().Be(-Math.Sign(local[i - 1].X - local[i - 2].X));
            local[i].X.Should().BeApproximately(local[i - 1].X, 0.5);
        }
    }

    [Fact]
    public void ConcaveZone_ClipsLanesIntoSeveralSegments_AllInsideThePolygon()
    {
        // U-shape like the seeded ZoneA: upper lanes cross both arms.
        var zone = Zone("U", (0, 0), (1500, 0), (1500, 1000), (1000, 1000), (1000, 350), (500, 350), (500, 1000), (0, 1000));
        var ring = Local(zone.Vertices);

        var route = SearchRoutePlanner.Plan(zone, "997", null, Defaults);
        var local = Local(route.Waypoints);

        // Some lane is split around the notch: a segment ending at the inner edge of an arm.
        local.Should().Contain(p => Math.Abs(p.X - 500) < 0.5 && p.Y > 350);
        local.Should().Contain(p => Math.Abs(p.X - 1000) < 0.5 && p.Y > 350);

        foreach (var p in local)
        {
            (Polygon.Contains(ring, p) || Polygon.DistanceToBoundary(ring, p) < 0.5)
                .Should().BeTrue($"waypoint {p} must be inside the zone");
        }

        // Every segment (pairs of waypoints) lies inside the polygon: its midpoint is inside.
        for (var i = 0; i < local.Count; i += 2)
            Polygon.Contains(ring, (local[i] + local[i + 1]) * 0.5).Should().BeTrue();
    }

    [Fact]
    public void SeededZoneA_PlansInsideItsPolygon()
    {
        var zone = SqliteAoiZoneStore.LoadSeed().Single(z => z.Name == "ZoneA");
        var projection = GeoProjection.Around(zone.Vertices);
        var ring = zone.Vertices.Select(projection.ToLocal).ToList();

        var route = SearchRoutePlanner.Plan(zone, "997", new GeoPoint(31.801447, 34.643497), Defaults);

        route.Waypoints.Should().HaveCountGreaterThan(4);
        route.LengthMeters.Should().BeGreaterThan(0);
        route.EstimatedDuration.Should().BeGreaterThan(TimeSpan.Zero);
        foreach (var p in route.Waypoints.Select(projection.ToLocal))
            (Polygon.Contains(ring, p) || Polygon.DistanceToBoundary(ring, p) < 0.5).Should().BeTrue();
    }

    [Theory]
    [InlineData(-500, -500)]   // south-west of the zone
    [InlineData(2500, -500)]   // south-east
    [InlineData(-500, 1500)]   // north-west
    [InlineData(2500, 1500)]   // north-east
    public void Route_StartsAtTheCornerNearestTheUav(double uavX, double uavY)
    {
        var zone = Zone("Rect", (0, 0), (2000, 0), (2000, 1000), (0, 1000));
        var uav = Frame.ToGeo(new Vec2(uavX, uavY));

        var first = Frame.ToLocal(SearchRoutePlanner.Plan(zone, "997", uav, Defaults).Waypoints[0]);

        Math.Sign(first.X - 1000).Should().Be(Math.Sign(uavX - 1000));
        Math.Sign(first.Y - 500).Should().Be(Math.Sign(uavY - 500));
    }

    [Fact]
    public void ZoneNarrowerThanOneLane_GetsASingleCentrelinePass()
    {
        var zone = Zone("Strip", (0, 0), (3000, 0), (3000, 100), (0, 100));

        var route = SearchRoutePlanner.Plan(zone, "997", null, Defaults);

        route.LaneCount.Should().Be(1);
        route.Waypoints.Should().HaveCount(2);
        Local(route.Waypoints).Should().OnlyContain(p => Math.Abs(p.Y - 50) < 0.5);
    }

    [Fact]
    public void RotatedZone_LanesRunAlongItsLongSide()
    {
        // A 2000 x 600 m rectangle rotated 30°.
        var (sin, cos) = Math.SinCos(Math.PI / 6);
        (double, double) R(double x, double y) => (x * cos - y * sin, x * sin + y * cos);
        var zone = Zone("Tilted", R(0, 0), R(2000, 0), R(2000, 600), R(0, 600));

        var route = SearchRoutePlanner.Plan(zone, "997", null, Defaults);

        route.LaneCount.Should().Be((int)Math.Ceiling(600 / Defaults.LaneSpacingMeters));
        var local = Local(route.Waypoints);
        var laneAngle = Math.Atan2(local[1].Y - local[0].Y, local[1].X - local[0].X);
        Math.Abs(Math.Sin(laneAngle - Math.PI / 6)).Should().BeLessThan(0.01, "lanes are parallel to the long side");
    }

    [Fact]
    public void TooManyWaypoints_IsRejected()
    {
        var zone = Zone("Big", (0, 0), (20000, 0), (20000, 20000), (0, 20000));

        var act = () => SearchRoutePlanner.Plan(zone, "997", null, Defaults with { MaxWaypoints = 10 });

        act.Should().Throw<SearchPlanException>().WithMessage("*over the limit of 10*");
    }

    [Fact]
    public void FewerThanThreeVertices_IsRejected()
    {
        var act = () => SearchRoutePlanner.Plan(Zone("Line", (0, 0), (100, 0)), "997", null, Defaults);

        act.Should().Throw<SearchPlanException>().WithMessage("*at least 3*");
    }

    [Fact]
    public void Projection_RoundTripsWellUnderAMeter()
    {
        var projection = new GeoProjection(new GeoPoint(31.81, 34.66));
        var far = new GeoPoint(31.85, 34.71); // ~6 km away

        var back = projection.ToGeo(projection.ToLocal(far));

        GeoProjection.DistanceMeters(far, back).Should().BeLessThan(0.01);

        // Projected distance vs. haversine over ~6 km.
        var local = projection.ToLocal(far).Length;
        Haversine(new GeoPoint(31.81, 34.66), far).Should().BeApproximately(local, 1.0);
    }

    private static double Haversine(GeoPoint a, GeoPoint b)
    {
        const double r = 6_371_008.8;
        var dLat = (b.Lat - a.Lat) * Math.PI / 180;
        var dLng = (b.Lng - a.Lng) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }
}

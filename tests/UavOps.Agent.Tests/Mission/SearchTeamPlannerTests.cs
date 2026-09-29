using FluentAssertions;
using UavOps.Agent.Mission;

namespace UavOps.Agent.Tests.Mission;

public class SearchTeamPlannerTests
{
    // 1000 ft, 60° HFOV, 20% overlap: lane spacing ~281.6 m (same as SearchRoutePlannerTests).
    private static readonly SearchPlanParameters Defaults = new(1000, 60, 0.2, 300, 100);
    private static readonly double Spacing = Defaults.LaneSpacingMeters;

    private static readonly GeoProjection Frame = new(new GeoPoint(31.81, 34.66));

    private static AoiZone Zone(string name, params (double X, double Y)[] meters) =>
        new(name, meters.Select(m => Frame.ToGeo(new Vec2(m.X, m.Y))).ToList());

    // 4000 m east-west, 3000 m north-south: lanes run east-west, 11 of them.
    private static readonly AoiZone Rect = Zone("Rect", (0, 0), (4000, 0), (4000, 3000), (0, 3000));

    private static SearchTeamMember Member(string tail, double x, double y, double speedKts = 100) =>
        new(tail, Frame.ToGeo(new Vec2(x, y)), 1000, speedKts);

    private static List<Vec2> Local(IEnumerable<GeoPoint> points) => points.Select(Frame.ToLocal).ToList();

    /// <summary>The distinct lane positions (y, rounded) a route flies.</summary>
    private static List<double> LaneYs(SearchRoute route) =>
        Local(route.Waypoints).Select(p => Math.Round(p.Y, 1)).Distinct().OrderBy(y => y).ToList();

    [Fact]
    public void TeamOfOne_IsTheSingleUavRoute()
    {
        var uav = Frame.ToGeo(new Vec2(-500, -500));

        var single = SearchRoutePlanner.Plan(Rect, "997", uav, Defaults);
        var team = SearchRoutePlanner.PlanTeam(Rect, [new SearchTeamMember("997", uav, 1000, 100)], Spacing, 300);

        team.Routes.Should().ContainSingle();
        team.Routes[0].Waypoints.Should().Equal(single.Waypoints);
        team.Routes[0].LaneCount.Should().Be(single.LaneCount);
        team.UnusedTails.Should().BeEmpty();
    }

    [Fact]
    public void TwoUavs_SplitTheLanes_WithNoGapAndNoLaneTwice()
    {
        var single = SearchRoutePlanner.Plan(Rect, "997", null, Defaults);

        var team = SearchRoutePlanner.PlanTeam(Rect, [Member("998", -500, -500), Member("999", -500, 3500)], Spacing, 300);

        team.Routes.Should().HaveCount(2);
        var laneSets = team.Routes.Select(LaneYs).ToList();
        laneSets.SelectMany(l => l).Should().OnlyHaveUniqueItems("no lane is searched by two UAVs");
        laneSets.SelectMany(l => l).OrderBy(y => y).Should().Equal(LaneYs(single), "together they fly exactly the single-UAV lanes");
        team.Routes.Sum(r => r.LaneCount).Should().Be(single.LaneCount);

        // Each band is one contiguous run of lanes.
        var (low, high) = laneSets[0].Max() < laneSets[1].Min() ? (laneSets[0], laneSets[1]) : (laneSets[1], laneSets[0]);
        (high.Min() - low.Max()).Should().BeApproximately(Spacing, 0.5, "the bands meet one lane spacing apart");
    }

    [Fact]
    public void EachUav_TakesTheBandNearIt()
    {
        // 998 south of the zone, 999 north of it.
        var team = SearchRoutePlanner.PlanTeam(Rect, [Member("998", 2000, -1000), Member("999", 2000, 4000)], Spacing, 300);

        var south = team.Routes.Single(r => r.TailNumber == "998");
        var north = team.Routes.Single(r => r.TailNumber == "999");
        LaneYs(south).Max().Should().BeLessThan(LaneYs(north).Min());
    }

    [Fact]
    public void EqualUavsAtEqualDistances_GetBalancedParts()
    {
        var team = SearchRoutePlanner.PlanTeam(Rect, [Member("998", 2000, -1000), Member("999", 2000, 4000)], Spacing, 300);

        var lengths = team.Routes.Select(r => r.LengthMeters).ToList();
        Math.Abs(lengths[0] - lengths[1]).Should().BeLessThan(4000 + Spacing, "at most one lane apart");
    }

    [Fact]
    public void FasterUav_TakesMoreLanes()
    {
        var team = SearchRoutePlanner.PlanTeam(Rect,
            [Member("998", 2000, -1000, speedKts: 200), Member("999", 2000, 4000, speedKts: 100)], Spacing, 300);

        team.Routes.Single(r => r.TailNumber == "998").LaneCount
            .Should().BeGreaterThan(team.Routes.Single(r => r.TailNumber == "999").LaneCount);
    }

    [Fact]
    public void ThreeUavs_EachGetTheirOwnBand()
    {
        var single = SearchRoutePlanner.Plan(Rect, "997", null, Defaults);

        var team = SearchRoutePlanner.PlanTeam(Rect,
            [Member("997", -500, 1500), Member("998", 2000, -1000), Member("999", 2000, 4000)], Spacing, 300);

        team.Routes.Should().HaveCount(3);
        team.Routes.Select(r => r.TailNumber).Should().BeEquivalentTo("997", "998", "999");
        team.Routes.SelectMany(LaneYs).OrderBy(y => y).Should().Equal(LaneYs(single));
        team.Routes.Should().OnlyContain(r => r.LaneCount >= 1);
    }

    [Fact]
    public void MoreUavsThanLanes_LeavesTheExtrasOut()
    {
        // Narrower than one lane: a single centreline pass, so only one UAV can be used.
        var strip = Zone("Strip", (0, 0), (3000, 0), (3000, 100), (0, 100));

        var team = SearchRoutePlanner.PlanTeam(strip, [Member("998", -500, 50), Member("999", 9000, 50)], Spacing, 300);

        team.Routes.Should().ContainSingle().Which.TailNumber.Should().Be("998", "998 is the nearer one");
        team.UnusedTails.Should().Equal("999");
    }

    [Fact]
    public void AUavSoFarItOnlySlowsTheTeam_IsLeftOut()
    {
        // 999 is ~80 km away: any lane it took would finish long after 998 has done the whole zone.
        var team = SearchRoutePlanner.PlanTeam(Rect, [Member("998", 2000, -1000), Member("999", 2000, 80000)], Spacing, 300);

        team.Routes.Should().ContainSingle().Which.TailNumber.Should().Be("998");
        team.UnusedTails.Should().Equal("999");
    }

    [Fact]
    public void AllRoutes_ShareTheTeamId_AndKeepTheirUavsAltitude()
    {
        var members = new[] { Member("998", 0, -500) with { AltitudeFt = 3000 }, Member("999", 0, 3500) with { AltitudeFt = 4000 } };

        var team = SearchRoutePlanner.PlanTeam(Rect, members, Spacing, 300);

        team.Routes.Should().OnlyContain(r => r.RouteId.EndsWith(team.TeamId));
        team.Routes.Select(r => r.RouteId).Should().OnlyHaveUniqueItems();
        team.Routes.Single(r => r.TailNumber == "998").AltitudeFt.Should().Be(3000);
        team.Routes.Single(r => r.TailNumber == "999").AltitudeFt.Should().Be(4000);
    }

    [Fact]
    public void ConcaveZone_SplitsWithoutLosingASegment()
    {
        var zone = Zone("U", (0, 0), (1500, 0), (1500, 1000), (1000, 1000), (1000, 350), (500, 350), (500, 1000), (0, 1000));
        var single = SearchRoutePlanner.Plan(zone, "997", null, Defaults);

        var team = SearchRoutePlanner.PlanTeam(zone, [Member("998", 750, -500), Member("999", 750, 1500)], Spacing, 300);

        team.Routes.Sum(r => r.Waypoints.Count).Should().Be(single.Waypoints.Count, "every segment is flown by exactly one UAV");
    }

    [Fact]
    public void TooManyWaypoints_NamesTheUavWhosePartIsTooLong()
    {
        var big = Zone("Big", (0, 0), (20000, 0), (20000, 20000), (0, 20000));

        var act = () => SearchRoutePlanner.PlanTeam(big, [Member("998", 0, -500), Member("999", 0, 20500)], Spacing, 10);

        act.Should().Throw<SearchPlanException>().WithMessage("*'s part of zone 'Big'*over the limit of 10*");
    }
}

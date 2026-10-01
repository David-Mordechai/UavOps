using FluentAssertions;
using UavOps.Agent.Mission;
using UavOps.Simulator;
using UavOps.Simulator.Camera;
using UavOps.Simulator.Map;
using UavOps.Simulator.World;

namespace UavOps.Agent.Tests.Simulator;

/// <summary>What moves on the simulated ground: the sim clock, the road graph, vehicles driving it,
/// and driving scenario objects - all a function of sim time.</summary>
public class GroundWorldTests
{
    private static readonly GeoProjection Frame = new(new GeoPoint(31.344, 35.035));
    private static readonly string ArchivePath = Path.Combine(RepoRoot(), "src", "UavOps.Simulator", "wwwroot", "map", "israel.pmtiles");

    // ----- Clock -----

    [Fact]
    public void Clock_ConvertsWallTimeToSimTime_BetweenTicks()
    {
        var clock = new SimClock();
        var t0 = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        clock.Advance(1, t0);
        clock.Advance(10, t0.AddSeconds(1));   // 10x for a second

        clock.Now.Should().Be(11);
        clock.At(t0.AddSeconds(0.5)).Should().BeApproximately(6, 1e-9);
        clock.At(t0.AddSeconds(-5)).Should().Be(1, "before what's remembered: the oldest");
        clock.At(t0.AddSeconds(9)).Should().Be(11, "after the last tick: now");
    }

    // ----- Driving a path -----

    private static DrivePath StraightNorth(double meters) => new([new Vec2(0, 0), new Vec2(0, meters / 2), new Vec2(0, meters)]);

    [Fact]
    public void Vehicle_DrivesAtItsSpeed_TurnsAtTheEnd_AndDrivesBack()
    {
        var car = new MovingVehicle("c", VehicleKind.Car, "red", StraightNorth(1000), SpeedMps: 10, StartAlong: 0, StartForward: true, LaneOffsetMeters: 0);

        car.At(0).Position.Y.Should().BeApproximately(0, 1e-6);
        car.At(50).Position.Y.Should().BeApproximately(500, 1e-6);
        car.At(50).HeadingDeg.Should().BeApproximately(0, 1e-6, "driving north");
        car.At(150).Position.Y.Should().BeApproximately(500, 1e-6, "turned at 1000 m and came back 500");
        Math.Abs(car.At(150).HeadingDeg).Should().BeApproximately(180, 1e-6, "driving south");
        car.At(200).Position.Y.Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void Vehicle_KeepsToTheRightOfItsDirection()
    {
        var car = new MovingVehicle("c", VehicleKind.Car, "red", StraightNorth(1000), 10, 0, true, LaneOffsetMeters: 2);

        car.At(10).Position.X.Should().BeApproximately(2, 1e-6, "north-bound, right is east");
        car.At(150).Position.X.Should().BeApproximately(-2, 1e-6, "south-bound, right is west");
    }

    [Fact]
    public void Vehicle_SameSimTime_SamePlace()
    {
        var car = new MovingVehicle("c", VehicleKind.Car, "red", StraightNorth(1000), 13.9, 321, false, 1.8);

        car.At(77.7).Should().Be(car.At(77.7));
    }

    // ----- The road graph -----

    [Fact]
    public void Clipping_CutsALineAtTheTileEdge()
    {
        var pieces = RoadNetwork.ClipToTile([(-100, 100), (200, 100), (200, 5000)], 4096).ToList();

        pieces.Should().ContainSingle();
        pieces[0][0].X.Should().BeApproximately(0, 1e-9);
        pieces[0][^1].Y.Should().BeApproximately(4096, 1e-9);
    }

    [Fact]
    public void RoadCutAtATileEdge_IsJoinedIntoOneRoad()
    {
        var roads = new RoadNetwork(Frame);
        // Two tiles each give their half, meeting within a metre of each other.
        roads.AddLine("primary", [new Vec2(0, 0), new Vec2(0, 500)]);
        roads.AddLine("primary", [new Vec2(0.6, 500.4), new Vec2(0, 1000)]);

        var drive = roads.Drive(0, 0, 5000, new Random(1), RoadNetwork.TrafficClasses);

        drive.Should().HaveCount(3, "one road, 0 → 500 → 1000");
        drive[^1].Y.Should().BeApproximately(1000, 1e-6);
    }

    [Fact]
    public void Drive_GoesStraightOnMoreOftenThanItTurns_AndNeverBack()
    {
        var roads = new RoadNetwork(Frame);
        roads.AddLine("primary", [new Vec2(0, -500), new Vec2(0, 0), new Vec2(0, 500)]);   // straight on
        roads.AddLine("minor", [new Vec2(0, 0), new Vec2(500, 0)]);                         // a right turn

        var straight = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var drive = roads.Drive(0, 0, 5000, new Random(seed), RoadNetwork.TrafficClasses);
            drive.Should().NotContain(p => p.Y < -500 - 1e-6);
            if (drive[^1].Y > 499)
                straight++;
        }
        straight.Should().BeGreaterThan(140);
    }

    // ----- Traffic -----

    [Fact]
    public void Traffic_NeverHasAWhiteSilverOrBeigeVan()
    {
        var roads = new RoadNetwork(Frame);
        for (var i = 0; i < 20; i++)
            roads.AddLine("primary", [new Vec2(i * 100, 0), new Vec2(i * 100, 5000)]);

        var traffic = new Traffic(roads, density: 1);

        traffic.All.Should().HaveCountGreaterThan(100);
        traffic.All.Where(v => v.Kind == VehicleKind.Van).Should().NotContain(v => v.Color == "white" || v.Color == "silver" || v.Color == "beige");
    }

    [Fact]
    public void Traffic_Near_FindsOnlyVehiclesInRange()
    {
        var roads = new RoadNetwork(Frame);
        roads.AddLine("primary", [new Vec2(0, 0), new Vec2(0, 20000)]);
        var traffic = new Traffic(roads, density: 1);

        var near = traffic.Near(new Vec2(0, 10000), 500, simTime: 42).ToList();

        near.Should().OnlyContain(n => Vec2.Distance(n.Pose.Position, new Vec2(0, 10000)) <= 500);
        near.Count.Should().BeLessThan(traffic.All.Count);
    }

    // ----- Driving scenario objects -----

    private static (GroundWorld World, SimClock Clock) WorldWithRoad(double speedKmh)
    {
        var roads = new RoadNetwork(Frame);
        roads.AddLine("track", [new Vec2(0, -3000), new Vec2(0, 3000)]);
        var scenario = new ScenarioStore(new ScenarioOptions
        {
            Objects = [new ScenarioObjectConfig { Id = "red-car", Label = "red car", Lat = Frame.ToGeo(new Vec2(20, 0)).Lat, Lng = Frame.ToGeo(new Vec2(20, 0)).Lng, SpeedKmh = speedKmh }]
        });
        var clock = new SimClock();
        return (new GroundWorld(scenario, clock, Frame, roads, traffic: null), clock);
    }

    [Fact]
    public void ADrivingObject_StartsOnTheRoadWhereItWasPlaced_AndMovesAlongIt()
    {
        var (world, _) = WorldWithRoad(speedKmh: 36);

        var start = Frame.ToLocal(Pos(world.ObjectsAt(0).Single()));
        var later = Frame.ToLocal(Pos(world.ObjectsAt(10).Single()));

        start.X.Should().BeApproximately(0, 1e-6, "moved onto the road (a track: one lane)");
        start.Y.Should().BeApproximately(0, 0.5);
        Math.Abs(later.Y - start.Y).Should().BeApproximately(100, 1, "36 km/h for 10 s");
        later.X.Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void AParkedObject_StaysWhereItWasPlaced()
    {
        var (world, _) = WorldWithRoad(speedKmh: 0);

        world.ObjectsAt(500).Single().Should().Be(world.ObjectsAt(0).Single());
    }

    [Fact]
    public void Traffic_IsLeftOffTheAerialPhotos_ButDrivingTargetsAreNot()
    {
        // No photos here: TrafficNear returns what Traffic.Near does.
        var roads = new RoadNetwork(Frame);
        roads.AddLine("primary", [new Vec2(0, 0), new Vec2(0, 5000)]);
        var world = new GroundWorld(new ScenarioStore(new ScenarioOptions()), new SimClock(), Frame, roads, new Traffic(roads, 1));

        world.TrafficNear(Frame.ToGeo(new Vec2(0, 2500)), 3000, 0).Should().NotBeEmpty();
    }

    // ----- Against the real offline map (skipped without it) -----

    [Fact]
    public void RealMap_TheRedCarsRoadInZoneA_CanBeDriven()
    {
        if (!File.Exists(ArchivePath))
            return;
        using var tiles = new PmTilesReader(ArchivePath);
        var roads = RoadNetwork.Load(tiles, Frame, [(new GeoPoint(31.345, 35.042), 3000)]);
        var redCar = new GeoPoint(31.344911, 35.048701);

        var nearest = roads.Nearest(Frame.ToLocal(redCar), GroundWorld.ObjectRoadSearchMeters, RoadNetwork.VehicleClasses);

        roads.Edges.Should().HaveCountGreaterThan(100);
        nearest.Should().NotBeNull("the red car is placed on the Yatir forest road");
        var edge = roads.Edges[nearest!.Value.Edge];
        roads.Drive(edge.A, nearest.Value.Edge, 2000, new Random(1), RoadNetwork.VehicleClasses)
            .Should().HaveCountGreaterThan(2, "the road goes on from there");
    }

    private static GeoPoint Pos(ScenarioObject o) => new(o.Lat, o.Lng);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UavOps.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("UavOps.sln not found above the test output folder.");
    }
}

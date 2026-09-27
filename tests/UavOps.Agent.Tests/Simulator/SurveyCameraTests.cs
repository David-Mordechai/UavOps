using FluentAssertions;
using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Onboard.Contracts;
using UavOps.Simulator;
using UavOps.Simulator.Camera;

namespace UavOps.Agent.Tests.Simulator;

/// <summary>The payload camera's survey: frames are taken by distance flown, only on the route at
/// search altitude, and the buffer hands them out in order.</summary>
public class SurveyCameraTests
{
    private static readonly SimOptions Options = new();

    // The search zoom: 20 degrees across (2x of the 40-degree payload), ~0.08 m per pixel at 1000 ft.
    private const double SurveyHfovDeg = 20;

    private static SimFleet SearchingFleet()
    {
        var scenario = new ScenarioStore(new ScenarioOptions());
        var fleet = new SimFleet(new SimulatedDetector(scenario, Options), Options);
        var zone = SqliteAoiZoneStore.LoadSeed().Single(z => z.Name == "ZoneA");
        UavOps.Agent.Contracts.KnownPoints.TryResolve("home", out var homeLat, out var homeLng);
        var route = SearchRoutePlanner.Plan(zone, "997", new GeoPoint(homeLat, homeLng),
            new SearchPlanParameters(1000, SurveyHfovDeg, 0.2, 300, 105));
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg);
        fleet.UploadWaypoints("997", route.Waypoints.Select(p => new Waypoint { Lat = p.Lat, Lng = p.Lng, AltitudeFt = route.AltitudeFt }).ToList());
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m1", ZoneName = "ZoneA", Prompt = "white van", MinConfidence = 0.5 });
        fleet.StartMission("997");
        return fleet;
    }

    private static List<FrameTelemetry> Fly(SimFleet fleet, double seconds, double step)
    {
        var frames = new List<FrameTelemetry>();
        for (var t = 0.0; t < seconds; t += step)
            frames.AddRange(fleet.Advance(step, DateTime.UtcNow).SurveyCaptures.Select(c => c.Telemetry));
        return frames;
    }

    [Theory]
    [InlineData(0.1)] // 1x
    [InlineData(3)]   // 30x
    public void FramesLeaveNoGap_HoweverFastTheSimRuns(double tickSeconds)
    {
        var frames = Fly(SearchingFleet(), 1800, tickSeconds);

        var frameHeight = CameraModel.GroundWidthFor(1000, SurveyHfovDeg) * Options.CameraHeight / Options.CameraWidth;
        frames.Should().HaveCountGreaterThan(50);
        Gaps(frames).Should().OnlyContain(d => d < frameHeight, "consecutive frames must overlap, or a strip of ground is never seen");
    }

    [Fact]
    public void FrameCount_BarelyDependsOnTheTimeScale()
    {
        var atOneX = Fly(SearchingFleet(), 1800, 0.1).Count;
        var atThirtyX = Fly(SearchingFleet(), 1800, 3).Count;

        // Waypoint turns are cut a little differently at different step sizes; nothing more.
        atThirtyX.Should().BeCloseTo(atOneX, (uint)(atOneX / 20));
    }

    private static IEnumerable<double> Gaps(List<FrameTelemetry> frames) =>
        frames.Zip(frames.Skip(1), (a, b) => GeoProjection.DistanceMeters(new GeoPoint(a.Lat, a.Lng), new GeoPoint(b.Lat, b.Lng)));

    [Fact]
    public void FramesAreTakenOnTheRoute_AtSearchAltitude_OverlappingAlongTrack()
    {
        var frames = Fly(SearchingFleet(), 1800, 1);

        frames.Should().OnlyContain(f => Math.Abs(f.AltitudeFt - 1000) <= 30, "none on the way down to search altitude");
        frames.Should().OnlyContain(f => f.MissionId == "m1");
        frames.Select(f => f.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();

        var frameHeight = CameraModel.GroundWidthFor(1000, SurveyHfovDeg) * Options.CameraHeight / Options.CameraWidth;
        // One every (1 - overlap) of a frame height; reaching a waypoint snaps the UAV onto it, up
        // to ArrivalRadiusMeters further, which still leaves the frames overlapping.
        Gaps(frames).Should().OnlyContain(d => d <= frameHeight * (1 - Options.SurveyFrameOverlap) * 1.05 + Options.ArrivalRadiusMeters);
        Gaps(frames).Should().OnlyContain(d => d < frameHeight, "consecutive frames overlap");
    }

    [Fact]
    public void NoFrames_WithoutASearch()
    {
        var fleet = new SimFleet(new SimulatedDetector(new ScenarioStore(new ScenarioOptions()), Options), Options);
        fleet.Navigate("997", "alpha");

        Fly(fleet, 600, 1).Should().BeEmpty();
    }

    [Fact]
    public async Task Buffer_HandsOutFramesInOrder_AndWaitsForTheNext()
    {
        var buffer = new SurveyFrameBuffer(Options);
        SurveyFrame Frame(long seq) => new(new FrameTelemetry(seq, DateTime.UtcNow, 31.8, 34.6, 1000, 0, 20, 1280, 960, "m1"), [1]);
        buffer.Add("997", Frame(1));
        buffer.Add("997", Frame(2));

        (await buffer.NextAfterAsync("997", 0, TimeSpan.Zero, CancellationToken.None))!.Telemetry.Seq.Should().Be(1);
        (await buffer.NextAfterAsync("997", 1, TimeSpan.Zero, CancellationToken.None))!.Telemetry.Seq.Should().Be(2);
        (await buffer.NextAfterAsync("997", 2, TimeSpan.FromMilliseconds(50), CancellationToken.None)).Should().BeNull();

        var waiting = buffer.NextAfterAsync("997", 2, TimeSpan.FromSeconds(5), CancellationToken.None);
        buffer.Add("997", Frame(3));
        (await waiting)!.Telemetry.Seq.Should().Be(3);
    }
}

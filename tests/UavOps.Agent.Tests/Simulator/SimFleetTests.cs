using FluentAssertions;
using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Onboard.Contracts;
using UavOps.Simulator;
using KnownPoints = UavOps.Agent.Contracts.KnownPoints;

namespace UavOps.Agent.Tests.Simulator;

/// <summary>The simulator's flight model and detector, driven by hand with fixed time steps (no
/// real clock): the same route McpMoav would plan over ZoneA, with a white van inside it.</summary>
public class SimFleetTests
{
    private static readonly SimOptions Options = new();

    // The search zoom: 20 degrees across (2x of the 40-degree payload).
    private const double SurveyHfovDeg = 20;

    private static (SimFleet Fleet, ScenarioStore Scenario) Create(params ScenarioObjectConfig[] objects)
    {
        var scenario = new ScenarioStore(new ScenarioOptions { Objects = [.. objects] });
        return (new SimFleet(new SimulatedDetector(scenario, Options), Options), scenario);
    }

    // On the Yatir road inside ZoneA, where the default scenario puts it.
    private static ScenarioObjectConfig WhiteVan => new() { Id = "van", Label = "white van", Lat = 31.348227, Lng = 35.052693 };

    private static GeoPoint Home => KnownPoints.TryResolve("home", out var lat, out var lng) ? new GeoPoint(lat, lng) : throw new InvalidOperationException();

    /// <summary>Runs sim time forward in 1 s steps, collecting everything reported.</summary>
    private static (List<DetectionReport> Detections, List<MissionEventReport> Events) Run(SimFleet fleet, double seconds)
    {
        var detections = new List<DetectionReport>();
        var events = new List<MissionEventReport>();
        for (var t = 0.0; t < seconds; t += 1)
        {
            var result = fleet.Advance(1, DateTime.UtcNow);
            detections.AddRange(result.Detections);
            events.AddRange(result.MissionEvents);
        }
        return (detections, events);
    }

    private static List<Waypoint> ZoneARoute(string tail = "997")
    {
        var zone = SqliteAoiZoneStore.LoadSeed().Single(z => z.Name == "ZoneA");
        var route = SearchRoutePlanner.Plan(zone, tail, Home, new SearchPlanParameters(1000, SurveyHfovDeg, 0.2, 300, 105));
        return route.Waypoints.Select(p => new Waypoint { Lat = p.Lat, Lng = p.Lng, AltitudeFt = route.AltitudeFt }).ToList();
    }

    private static void PrepareSearch(SimFleet fleet, string prompt = "white van")
    {
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg);
        fleet.UploadWaypoints("997", ZoneARoute()).Success.Should().BeTrue();
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m1", ZoneName = "ZoneA", Prompt = prompt, MinConfidence = 0.5 });
    }

    [Fact]
    public void Navigate_FliesThereOverTime_ThenOrbits()
    {
        var (fleet, _) = Create();

        fleet.Navigate("997", "alpha").Value.Mode.Should().Be("Transiting");
        var start = fleet.GetTelemetry("997").Value;

        Run(fleet, 10);
        var moving = fleet.GetTelemetry("997").Value;
        GeoProjection.DistanceMeters(new GeoPoint(start.Lat, start.Lng), new GeoPoint(moving.Lat, moving.Lng))
            .Should().BeApproximately(10 * 105 * 0.514444, 20, "it flies at its speed, it doesn't teleport");

        Run(fleet, 600);
        var there = fleet.GetTelemetry("997").Value;
        there.Mode.Should().Be("Orbiting");
        KnownPoints.TryResolve("alpha", out var alphaLat, out var alphaLng);
        GeoProjection.DistanceMeters(new GeoPoint(there.Lat, there.Lng), new GeoPoint(alphaLat, alphaLng))
            .Should().BeLessThan(2 * Options.OrbitRadiusMeters + 1, "it holds near where it was sent");
    }

    [Fact]
    public void SpeedAndAltitude_ChangeGradually()
    {
        var (fleet, _) = Create();

        fleet.SetSpeed("997", 200);
        fleet.SetAltitude("997", 3000);
        Run(fleet, 2);

        var telemetry = fleet.GetTelemetry("997").Value;
        telemetry.SpeedKts.Should().BeInRange(106, 199);
        telemetry.AltitudeFt.Should().BeInRange(3001, 3999);

        Run(fleet, 60);
        fleet.GetTelemetry("997").Value.SpeedKts.Should().Be(200);
        fleet.GetTelemetry("997").Value.AltitudeFt.Should().Be(3000);
    }

    [Fact]
    public void ReturnToLaunch_FliesHomeAndLands()
    {
        var (fleet, _) = Create();
        fleet.Navigate("997", "alpha");
        Run(fleet, 300);

        fleet.ReturnToLaunch("997").Value.Mode.Should().Be("ReturningToLaunch");
        Run(fleet, 600);

        var telemetry = fleet.GetTelemetry("997").Value;
        telemetry.Mode.Should().Be("Landed");
        telemetry.Lat.Should().BeApproximately(Home.Lat, 1e-6);
        telemetry.Lng.Should().BeApproximately(Home.Lng, 1e-6);
    }

    [Fact]
    public void Upload_DoesNotStart_StartMissionDoes()
    {
        var (fleet, _) = Create();
        PrepareSearch(fleet);

        fleet.GetMissionStatus("997").Value.Mode.Should().Be("Orbiting");

        fleet.StartMission("997").Value.Mode.Should().Be("Searching");
        fleet.GetMissionStatus("997").Value.CurrentWaypointIndex.Should().Be(0);
    }

    [Fact]
    public void StartMission_WithNoRoute_Fails()
    {
        var (fleet, _) = Create();

        fleet.StartMission("997").Success.Should().BeFalse();
    }

    [Fact]
    public void FlyingZoneAWithTheVanInside_ReportsItExactlyOnce_ThenCompletes()
    {
        var (fleet, _) = Create(WhiteVan);
        PrepareSearch(fleet);
        fleet.StartMission("997");

        var (detections, events) = Run(fleet, 3 * 3600);

        detections.Should().ContainSingle();
        var detection = detections[0];
        detection.TailNumber.Should().Be("997");
        detection.MissionId.Should().Be("m1");
        detection.ZoneName.Should().Be("ZoneA");
        detection.Prompt.Should().Be("white van");
        GeoProjection.DistanceMeters(new GeoPoint(detection.Lat, detection.Lng), new GeoPoint(WhiteVan.Lat, WhiteVan.Lng))
            .Should().BeLessThan(10, "the report is the van's position, give or take sensor noise");

        events.Should().ContainSingle().Which.Kind.Should().Be(MissionEventKinds.Completed);
        var status = fleet.GetMissionStatus("997").Value;
        status.Mode.Should().Be("Orbiting");
        status.CurrentWaypointIndex.Should().BeNull();
    }

    [Theory]
    [InlineData("red car")]   // nothing matches
    [InlineData("white car")] // every word must match: the van isn't a car
    public void SearchingForSomethingNotThere_FindsNothing(string prompt)
    {
        var (fleet, _) = Create(WhiteVan);
        PrepareSearch(fleet, prompt);
        fleet.StartMission("997");

        var (detections, events) = Run(fleet, 3 * 3600);

        detections.Should().BeEmpty();
        events.Should().ContainSingle().Which.Kind.Should().Be(MissionEventKinds.Completed);
    }

    [Fact]
    public void RedirectingASearchingUav_AbortsTheMission()
    {
        var (fleet, _) = Create();
        PrepareSearch(fleet);
        fleet.StartMission("997");
        Run(fleet, 30);

        fleet.Navigate("997", "bravo");
        var (_, events) = Run(fleet, 1);

        events.Should().ContainSingle().Which.Kind.Should().Be(MissionEventKinds.Aborted);
        fleet.GetMissionStatus("997").Value.Mode.Should().Be("Transiting");
    }

    [Fact]
    public void NavigateAcceptsALatLngLiteral()
    {
        var (fleet, _) = Create();

        fleet.Navigate("998", "31.348227,35.052693").Success.Should().BeTrue();
        Run(fleet, 900);

        var telemetry = fleet.GetTelemetry("998").Value;
        GeoProjection.DistanceMeters(new GeoPoint(telemetry.Lat, telemetry.Lng), new GeoPoint(31.348227, 35.052693))
            .Should().BeLessThan(2 * Options.OrbitRadiusMeters + 1);
    }

    [Fact]
    public void PointPayload_CentresTheLiveCameraOnThePoint_WithoutZooming_AndResetReturnsItStraightDown()
    {
        var (fleet, _) = Create();

        fleet.PointPayload("997", "31.344911,35.048701").Success.Should().BeTrue();
        var locked = fleet.CameraNow("997", DateTime.UtcNow)!;
        locked.Lat.Should().BeApproximately(31.344911, 1e-9);
        locked.Lng.Should().BeApproximately(35.048701, 1e-9);
        locked.HFovDeg.Should().Be(Options.PayloadWideHorizontalFovDeg, "pointing never zooms; only SetPayloadZoom does");
        var uav = fleet.CameraNow("997", DateTime.UtcNow, nadir: true)!;
        GeoProjection.DistanceMeters(new GeoPoint(uav.Lat, uav.Lng), new GeoPoint(31.344911, 35.048701))
            .Should().BeGreaterThan(1000, "the zoom endpoint still measures range from the UAV itself");

        fleet.ResetPayload("997");
        var down = fleet.CameraNow("997", DateTime.UtcNow)!;
        down.Should().BeEquivalentTo(fleet.CameraNow("997", DateTime.UtcNow, nadir: true)!, o => o.Excluding(f => f.CapturedAtUtc));
    }

    [Fact]
    public void SendToAPointAndPointThePayloadThere_ItOrbitsAroundIt_WithThePayloadOnIt()
    {
        var (fleet, _) = Create();
        var target = new GeoPoint(31.344911, 35.048701);

        // "send 997 to the red car and point the payload there": the two tool calls.
        fleet.Navigate("997", "31.344911,35.048701");
        fleet.PointPayload("997", "31.344911,35.048701");
        Run(fleet, 900);

        var telemetry = fleet.GetTelemetry("997").Value;
        telemetry.Mode.Should().Be("Orbiting");
        telemetry.PayloadLockedOn.Should().Be("31.344911,35.048701");
        for (var i = 0; i < 5; i++)
        {
            Run(fleet, 7);
            var now = fleet.GetTelemetry("997").Value;
            GeoProjection.DistanceMeters(new GeoPoint(now.Lat, now.Lng), target)
                .Should().BeApproximately(Options.OrbitRadiusMeters, 5, "the circle is centred on where it was sent");
            var camera = fleet.CameraNow("997", DateTime.UtcNow)!;
            GeoProjection.DistanceMeters(new GeoPoint(camera.Lat, camera.Lng), target).Should().BeLessThan(0.5);
            camera.HeadingDeg.Should().Be(0, "the locked view stays still (north-up) while the UAV circles");
        }
    }

    [Fact]
    public void SetPayloadZoom_NarrowsTheCamera_ClampsToThePayload_AndReportsIt()
    {
        var (fleet, _) = Create();

        var zoomed = fleet.SetPayloadZoom("997", 8).Value;
        zoomed.PayloadZoom.Should().Be(8);
        zoomed.PayloadHfovDeg.Should().Be(Options.PayloadWideHorizontalFovDeg / 8);
        fleet.CameraNow("997", DateTime.UtcNow)!.HFovDeg.Should().Be(Options.PayloadWideHorizontalFovDeg / 8);
        zoomed.AltitudeFt.Should().Be(4000, "zooming never touches the altitude");

        fleet.SetPayloadZoom("997", 1000).Value.PayloadZoom.Should().Be(Options.PayloadMaxZoom);
        fleet.SetPayloadZoom("997", 0).Success.Should().BeFalse();
    }

    [Fact]
    public void ARouteAtTheCurrentAltitude_IsFlownWithoutChangingAltitude()
    {
        var (fleet, _) = Create(WhiteVan);
        var zone = SqliteAoiZoneStore.LoadSeed().Single(z => z.Name == "ZoneA");
        var route = SearchRoutePlanner.Plan(zone, "997", Home, new SearchPlanParameters(4000, 5, 0.2, 300, 105));
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / 5);
        fleet.UploadWaypoints("997", route.Waypoints.Select(p => new Waypoint { Lat = p.Lat, Lng = p.Lng, AltitudeFt = 4000 }).ToList());
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m1", ZoneName = "ZoneA", Prompt = "white van", MinConfidence = 0.5 });
        fleet.StartMission("997");

        var detections = new List<DetectionReport>();
        for (var i = 0; i < 3 * 120; i++)
        {
            detections.AddRange(Run(fleet, 30).Detections);
            fleet.GetTelemetry("997").Value.AltitudeFt.Should().Be(4000);
        }
        detections.Should().ContainSingle("the van is still found from 4000 ft, zoomed in");
    }

    /// <summary>An onboard agent still working through frames after the route is flown.</summary>
    private sealed class StillAnalysingDetector : IOnboardDetector
    {
        public bool Finished { get; set; }
        public IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc) => [];
        public bool HasFinished(SimUav uav) => Finished;
    }

    [Theory]
    [InlineData(false, MissionEventKinds.Aborted)]
    [InlineData(true, MissionEventKinds.Completed)]
    public void ANewSearchWhileTheLastOneIsStillBeingAnalysed_ClosesTheLastOneUnderItsOwnId(bool analysisFinished, string kind)
    {
        // Reported live: the white-pickup search's end came out as the red-car search's
        // ("finished searching ZoneA - no red car found").
        var detector = new StillAnalysingDetector();
        var fleet = new SimFleet(detector, Options);
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg);
        fleet.UploadWaypoints("997", ZoneARoute());
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "pickup", ZoneName = "ZoneA", Prompt = "white pickup", MinConfidence = 0.5 });
        fleet.StartMission("997");
        Run(fleet, 3 * 3600).Events.Should().BeEmpty("the route is flown but its frames are still being looked at");
        detector.Finished = analysisFinished;

        fleet.UploadWaypoints("997", ZoneARoute());
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "redcar", ZoneName = "ZoneA", Prompt = "red car", MinConfidence = 0.5 });
        var events = Run(fleet, 60).Events;

        events.Should().ContainSingle();
        events[0].MissionId.Should().Be("pickup");
        events[0].Kind.Should().Be(kind);
    }

    [Fact]
    public void StartingASearch_PutsThePayloadBackStraightDown()
    {
        var (fleet, _) = Create();
        fleet.PointPayload("997", "alpha");
        PrepareSearch(fleet);

        fleet.StartMission("997");

        fleet.GetTelemetry("997").Value.PayloadLockedOn.Should().BeNull();
    }

    [Fact]
    public void AnObjectPlacedLater_IsFoundToo()
    {
        var (fleet, scenario) = Create();
        scenario.Add("white van", null, 31.344911, 35.048701); // on the road at the west end of ZoneA
        PrepareSearch(fleet);
        fleet.StartMission("997");

        var (detections, _) = Run(fleet, 3 * 3600);

        detections.Should().ContainSingle();
    }

    [Fact]
    public void Reset_PutsEveryUavBackAtItsStart_AndAbortsASearchInProgress()
    {
        var (fleet, _) = Create(WhiteVan);
        PrepareSearch(fleet);
        fleet.StartMission("997");
        fleet.Navigate("998", "bravo");
        Run(fleet, 120);

        fleet.Reset();
        var events = fleet.Advance(0, DateTime.UtcNow).MissionEvents;

        events.Should().ContainSingle().Which.Kind.Should().Be(MissionEventKinds.Aborted);
        foreach (var (tail, lat, lng) in new[] { ("997", 31.344000, 35.035000), ("998", 31.342500, 35.033500), ("999", 31.345500, 35.036500) })
        {
            var telemetry = fleet.GetTelemetry(tail).Value;
            telemetry.Lat.Should().BeApproximately(lat, 1e-6);
            telemetry.Lng.Should().BeApproximately(lng, 1e-6);
            telemetry.Mode.Should().Be("Orbiting");
            telemetry.AltitudeFt.Should().Be(4000);
            fleet.GetMissionStatus(tail).Value.WaypointCount.Should().Be(0);
            fleet.GetMissionStatus(tail).Value.SearchPrompt.Should().BeNull();
        }
        fleet.View().Detections.Should().BeEmpty();
    }

    [Fact]
    public void ARepeatingSearch_FliesTheRouteAgain_UntilStopped()
    {
        var (fleet, _) = Create();
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg);
        fleet.UploadWaypoints("997", ZoneARoute()).Success.Should().BeTrue();
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m1", ZoneName = "ZoneA", Prompt = "red car", MinConfidence = 0.5, Repeat = true });
        fleet.StartMission("997");

        var (_, events) = Run(fleet, 3 * 3600);

        events.Should().Contain(e => e.Kind == MissionEventKinds.PassCompleted);
        events.Should().NotContain(e => e.Kind == MissionEventKinds.Completed, "a repeating search never ends by itself");
        fleet.GetMissionStatus("997").Value.Mode.Should().Be("Searching");

        fleet.StopMission("997").Value.Mode.Should().Be("Orbiting");
        var (_, after) = Run(fleet, 5);
        after.Should().ContainSingle().Which.Kind.Should().Be(MissionEventKinds.Aborted);
        fleet.View().Uavs.Single(u => u.TailNumber == "997").Looking.Should().BeFalse("the onboard agent stops looking");
    }

    /// <summary>Plays back onboard-computer target reports, as the real client would hand them over.</summary>
    private sealed class ScriptedTracks : IOnboardDetector
    {
        public Queue<TargetTrackReport> Reports { get; } = new();
        public IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc) => [];
        public bool HasFinished(SimUav uav) => true;
        public TargetTrackReport? TakeTrack(SimUav uav) => Reports.Count > 0 ? Reports.Dequeue() : null;
    }

    private static TargetTrackReport Report(string state) =>
        new("997", "m1", "ZoneA", "red car", "T-1", "red car", state, 31.3485, 35.0527, 3, 90, 0.8, DateTime.UtcNow);

    [Fact]
    public void ATargetGivenUp_SendsAFollowingUav_BackToItsRoute_ButNeverResetsASearch()
    {
        var tracks = new ScriptedTracks();
        var fleet = new SimFleet(tracks, Options);
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg);
        fleet.UploadWaypoints("997", ZoneARoute()).Success.Should().BeTrue();
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m1", ZoneName = "ZoneA", Prompt = "red car", MinConfidence = 0.5, Track = true, Repeat = true });
        fleet.StartMission("997");
        Run(fleet, 600);
        var searching = fleet.GetMissionStatus("997").Value;
        searching.Mode.Should().Be("Searching");

        // Released while searching (an onboard restart's resync): nothing changes.
        tracks.Reports.Enqueue(Report(TargetTrackStates.Released));
        Run(fleet, 1);
        fleet.GetMissionStatus("997").Value.CurrentWaypointIndex.Should().BeGreaterThanOrEqualTo(searching.CurrentWaypointIndex!.Value);

        tracks.Reports.Enqueue(Report(TargetTrackStates.Tracking));
        Run(fleet, 2);
        fleet.GetMissionStatus("997").Value.Mode.Should().Be("Following");

        fleet.OnboardZoom("997", 60);    // the onboard computer zoomed in on the target
        fleet.OnboardRelease("997");      // and let go (straight down, widest)
        tracks.Reports.Enqueue(Report(TargetTrackStates.Released));
        var (_, events) = Run(fleet, 2);
        fleet.GetMissionStatus("997").Value.Mode.Should().Be("Searching");
        events.Should().Contain(e => e.Kind == MissionEventKinds.SearchResumed);
        fleet.GetTelemetry("997").Value.PayloadZoom.Should().BeApproximately(Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg, 0.01,
            "back on the route, the payload is at the search zoom again");
    }

    [Fact]
    public void AFollowedTargetsUpdates_AreOneEntryOnThePage_AtItsLatestPosition()
    {
        var tracks = new ScriptedTracks();
        var fleet = new SimFleet(tracks, Options);
        fleet.SetPayloadZoom("997", Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg);
        fleet.UploadWaypoints("997", ZoneARoute()).Success.Should().BeTrue();
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m1", ZoneName = "ZoneA", Prompt = "red car", MinConfidence = 0.5, Track = true, Repeat = true });
        fleet.StartMission("997");
        Run(fleet, 5);

        for (var k = 0; k < 4; k++)   // 4 reports, 100 m apart: each one goes to the ground
        {
            tracks.Reports.Enqueue(Report(TargetTrackStates.Tracking) with { Lat = 31.3485 + k * 100 / 111195.0 });
            Run(fleet, 1);
        }

        var entry = fleet.View().Detections.Should().ContainSingle().Subject;
        entry.TrackId.Should().Be("T-1");
        entry.Updates.Should().Be(4);
        entry.Lat.Should().BeApproximately(31.3485 + 300 / 111195.0, 1e-6, "the latest position");
    }

    [Fact]
    public void ALateReleaseFromAFinishedTask_DoesNotUndoTheNextSearchsZoom()
    {
        var (fleet, _) = Create();
        var searchZoom = Options.PayloadWideHorizontalFovDeg / SurveyHfovDeg;
        fleet.SetPayloadZoom("997", searchZoom);                     // PrepareAoiSearch zooms for the lanes
        fleet.UploadWaypoints("997", ZoneARoute()).Success.Should().BeTrue();
        fleet.SetSearchTarget("997", new SearchTargetRequest { MissionId = "m2", ZoneName = "ZoneA", Prompt = "red car", MinConfidence = 0.5, Track = true, Repeat = true });

        fleet.OnboardRelease("997");                                 // the previous task's release, arriving late
        fleet.GetTelemetry("997").Value.PayloadZoom.Should().BeApproximately(searchZoom, 0.01);

        fleet.StartMission("997");
        Run(fleet, 5);
        fleet.OnboardRelease("997");                                 // and during the search
        fleet.GetTelemetry("997").Value.PayloadZoom.Should().BeApproximately(searchZoom, 0.01);
    }
}

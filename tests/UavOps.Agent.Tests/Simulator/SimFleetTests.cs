using FluentAssertions;
using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Simulator;

namespace UavOps.Agent.Tests.Simulator;

/// <summary>The simulator's flight model and detector, driven by hand with fixed time steps (no
/// real clock): the same route McpMoav would plan over ZoneA, with a white van inside it.</summary>
public class SimFleetTests
{
    private static readonly SimOptions Options = new();

    private static (SimFleet Fleet, ScenarioStore Scenario) Create(params ScenarioObjectConfig[] objects)
    {
        var scenario = new ScenarioStore(new ScenarioOptions { Objects = [.. objects] });
        return (new SimFleet(new SimulatedDetector(scenario, Options), Options), scenario);
    }

    private static ScenarioObjectConfig WhiteVan => new() { Id = "van", Label = "white van", Lat = 31.81380, Lng = 34.66521 };

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
        var route = SearchRoutePlanner.Plan(zone, tail, new GeoPoint(31.801447, 34.643497), new SearchPlanParameters(1000, 60, 0.2, 300, 105));
        return route.Waypoints.Select(p => new Waypoint { Lat = p.Lat, Lng = p.Lng, AltitudeFt = route.AltitudeFt }).ToList();
    }

    private static void PrepareSearch(SimFleet fleet, string prompt = "white van")
    {
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
        GeoProjection.DistanceMeters(new GeoPoint(there.Lat, there.Lng), new GeoPoint(31.812, 34.66))
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
        telemetry.Lat.Should().BeApproximately(31.801447, 1e-6);
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

        fleet.Navigate("998", "31.81380,34.66521").Success.Should().BeTrue();
        Run(fleet, 900);

        var telemetry = fleet.GetTelemetry("998").Value;
        GeoProjection.DistanceMeters(new GeoPoint(telemetry.Lat, telemetry.Lng), new GeoPoint(31.8138, 34.66521))
            .Should().BeLessThan(2 * Options.OrbitRadiusMeters + 1);
    }

    [Fact]
    public void AnObjectPlacedLater_IsFoundToo()
    {
        var (fleet, scenario) = Create();
        scenario.Add("white van", null, 31.80900, 34.65500);
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
        foreach (var (tail, lat, lng) in new[] { ("997", 31.801447, 34.643497), ("998", 31.798000, 34.639000), ("999", 31.805000, 34.648000) })
        {
            var telemetry = fleet.GetTelemetry(tail).Value;
            telemetry.Lat.Should().BeApproximately(lat, 1e-6);
            telemetry.Lng.Should().BeApproximately(lng, 1e-6);
            telemetry.Mode.Should().Be("Orbiting");
            telemetry.AltitudeFt.Should().Be(4000);
            fleet.GetMissionStatus(tail).Value.WaypointCount.Should().Be(0);
            fleet.GetMissionStatus(tail).Value.SearchPrompt.Should().BeNull();
        }
        fleet.View(_ => 0).Detections.Should().BeEmpty();
    }
}

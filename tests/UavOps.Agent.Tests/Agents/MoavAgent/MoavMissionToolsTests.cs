using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpMoav;
using UavOps.Agent.Mission;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

/// <summary>The AOI search-mission tools on the simulated backend, checked against the
/// backend's own mission state rather than the tools' result text.</summary>
public sealed class MoavMissionToolsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "uavops-mission-tools-" + Guid.NewGuid().ToString("N"));
    private readonly SimulatedUavOperationService _moav = new();
    private readonly InMemoryRouteStore _routes = new();
    private readonly MissionOptions _options = new();
    private readonly DetectionPointRegistry _detections = new();
    private readonly RecordingOperatorNotifier _notifier = new();
    private readonly MissionEventService _missionEvents;
    private readonly SqliteAoiZoneStore _zones;

    public MoavMissionToolsTests()
    {
        _zones = new SqliteAoiZoneStore(Path.Combine(_directory, "aoi.db"));
        _missionEvents = new MissionEventService(_notifier, _detections, _options, NullLogger<MissionEventService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task<MissionStatus> MissionStatusOf(string tailNumber) =>
        (MissionStatus)(await _moav.GetMissionStatus(tailNumber, CancellationToken.None)).Value!;

    [Fact]
    public async Task PrepareAoiSearch_UploadsRouteAndSetsTarget_WithoutStarting()
    {
        var result = await MoavTools.PrepareAoiSearch(_moav, _zones, _routes, _options, _missionEvents, "997", "zone a", "white van", CancellationToken.None);

        result.Should().NotStartWith("Error");
        var json = JsonDocument.Parse(result).RootElement;
        json.GetProperty("started").GetBoolean().Should().BeFalse();
        json.GetProperty("zoneName").GetString().Should().Be("ZoneA");

        var status = await MissionStatusOf("997");
        status.WaypointCount.Should().Be(_routes.Get("997")!.Waypoints.Count).And.BeGreaterThan(0);
        status.SearchPrompt.Should().Be("white van");
        status.ActiveMissionId.Should().Be(_routes.Get("997")!.RouteId);
        status.Mode.Should().NotBe("Searching");
    }

    [Fact]
    public async Task StartMission_AfterPrepare_StartsSearching()
    {
        await MoavTools.PrepareAoiSearch(_moav, _zones, _routes, _options, _missionEvents, "997", "ZoneA", "white van", CancellationToken.None);

        var result = await MoavTools.StartMission(_moav, "997", CancellationToken.None);

        result.Should().Contain("\"mode\":\"Searching\"");
        (await MissionStatusOf("997")).Mode.Should().Be("Searching");
    }

    [Fact]
    public async Task StartMission_WithNoRoute_Fails()
    {
        var result = await MoavTools.StartMission(_moav, "998", CancellationToken.None);

        result.Should().StartWith("Error:").And.Contain("no route");
    }

    [Fact]
    public async Task PrepareAoiSearch_UnknownZone_NamesTheKnownOnes_AndChangesNothing()
    {
        var result = await MoavTools.PrepareAoiSearch(_moav, _zones, _routes, _options, _missionEvents, "997", "ZoneZ", "white van", CancellationToken.None);

        result.Should().Be("Error: Unknown AOI zone 'ZoneZ'. Known zones: ZoneA, ZoneB.");
        (await MissionStatusOf("997")).WaypointCount.Should().Be(0);
    }

    [Fact]
    public async Task PlanSearchRoute_DoesNotUpload_ThenUploadRouteDoes()
    {
        var plan = await MoavTools.PlanSearchRoute(_moav, _zones, _routes, _options, "998", "ZoneB", 1500, CancellationToken.None);

        JsonDocument.Parse(plan).RootElement.GetProperty("altitudeFt").GetInt32().Should().Be(1500);
        (await MissionStatusOf("998")).WaypointCount.Should().Be(0);

        var upload = await MoavTools.UploadRoute(_moav, _routes, "998", CancellationToken.None);

        upload.Should().Contain("\"started\":false");
        (await MissionStatusOf("998")).WaypointCount.Should().Be(_routes.Get("998")!.Waypoints.Count);
    }

    [Fact]
    public async Task UploadRoute_WithNothingPlanned_Fails()
    {
        (await MoavTools.UploadRoute(_moav, _routes, "999", CancellationToken.None)).Should().StartWith("Error: No search route");
    }

    [Fact]
    public async Task SetSearchTarget_WithoutARoute_StillSetsTheTarget()
    {
        await MoavTools.SetSearchTarget(_moav, _routes, _options, _missionEvents, "999", " red car ", CancellationToken.None);

        (await MissionStatusOf("999")).SearchPrompt.Should().Be("red car");
    }

    [Fact]
    public async Task SetSearchTarget_IsRemembered_ForTheMissionEndSummary()
    {
        await MoavTools.PrepareAoiSearch(_moav, _zones, _routes, _options, _missionEvents, "997", "ZoneA", "white van", CancellationToken.None);

        await _missionEvents.HandleMissionEventAsync(
            new MissionEventReport("997", _routes.Get("997")!.RouteId, "ZoneA", MissionEventKinds.Completed));

        _notifier.Posted.Select(p => p.Message).Should().Equal("997 finished searching ZoneA - no white van found.");
    }

    [Theory]
    [InlineData("the white van")]
    [InlineData("White Van")]
    [InlineData("detection 1")]
    public async Task Navigate_ToADetectionName_FliesToItsReportedPosition(string location)
    {
        _detections.Register(31.81234, 34.66123, "white van", "detection 1");

        var result = await MoavTools.Navigate(_moav, _detections, "998", location, CancellationToken.None);

        result.Should().StartWith("Note - ").And.Contain("31.81234,34.66123");
        var uav = (TelemetrySnapshot)(await _moav.GetTelemetry("998", CancellationToken.None)).Value!;
        uav.Lat.Should().BeApproximately(31.81234, 1e-6);
        uav.Lng.Should().BeApproximately(34.66123, 1e-6);
    }

    [Fact]
    public async Task Navigate_ToAKnownPoint_IsNeverShadowedByADetection()
    {
        _detections.Register(1, 1, "alpha");

        await MoavTools.Navigate(_moav, _detections, "998", "alpha", CancellationToken.None);

        KnownPoints.TryResolve("alpha", out var lat, out var lng);
        var uav = (TelemetrySnapshot)(await _moav.GetTelemetry("998", CancellationToken.None)).Value!;
        (uav.Lat, uav.Lng).Should().Be((lat, lng));
    }

    [Fact]
    public async Task UnknownTailNumber_IsReportedNotPlanned()
    {
        var result = await MoavTools.PrepareAoiSearch(_moav, _zones, _routes, _options, _missionEvents, "123", "ZoneA", "white van", CancellationToken.None);

        result.Should().StartWith("Error:");
        _routes.Get("123").Should().BeNull();
    }

    [Fact]
    public async Task ListAoiZones_ListsTheSeededZones()
    {
        var result = await MoavTools.ListAoiZones(_zones, CancellationToken.None);

        JsonDocument.Parse(result).RootElement.EnumerateArray().Select(z => z.GetProperty("name").GetString())
            .Should().Equal("ZoneA", "ZoneB");
    }

    /// <summary>The real ToolsConfig.yaml against McpMoav's real registrations: every tool builds,
    /// and injected services never show up as arguments the model must fill in.</summary>
    [Fact]
    public void ToolsConfig_BuildsEveryMoavTool_WithOnlyModelFacingParameters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOperationService>(_moav);
        services.AddSingleton(_options);
        services.AddSingleton<IAoiZoneStore>(_zones);
        services.AddSingleton<IRouteStore>(_routes);
        services.AddSingleton(_detections);
        services.AddSingleton(_missionEvents);
        var config = McpToolsConfigLoader.Load(Path.Combine(RepoRoot(), "src", "UavOps.Agent.McpMoav", "ToolsConfig.yaml"));

        var tools = McpToolsBuilder.Build(typeof(MoavTools), config, services).ToDictionary(t => t.ProtocolTool.Name);

        Properties(tools["PrepareAoiSearch"]).Should().BeEquivalentTo("tailNumber", "zoneName", "targetDescription");
        Properties(tools["PlanSearchRoute"]).Should().BeEquivalentTo("tailNumber", "zoneName", "altitudeFt");
        Required(tools["PlanSearchRoute"]).Should().BeEquivalentTo("tailNumber", "zoneName");
        Properties(tools["ListAoiZones"]).Should().BeEmpty();
        Properties(tools["Navigate"]).Should().BeEquivalentTo("tailNumber", "location");
        Properties(tools["PointPayload"]).Should().BeEquivalentTo("tailNumber", "location");
        Properties(tools["SetSearchTarget"]).Should().BeEquivalentTo("tailNumber", "targetDescription");

        // The operator's own "start the mission" is the approval; a gate prompt on top meant approving twice.
        tools["StartMission"].ProtocolTool.Annotations!.DestructiveHint.Should().BeFalse();
        tools["PrepareAoiSearch"].ProtocolTool.Annotations!.DestructiveHint.Should().BeFalse();
        tools["ListAoiZones"].ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    private static IEnumerable<string> Properties(ModelContextProtocol.Server.McpServerTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    private static IEnumerable<string> Required(ModelContextProtocol.Server.McpServerTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UavOps.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("UavOps.sln not found above the test output folder.");
    }
}

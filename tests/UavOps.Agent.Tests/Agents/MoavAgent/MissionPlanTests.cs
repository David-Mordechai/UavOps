using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpMoav;
using UavOps.Agent.McpMoav.Missions;
using UavOps.Agent.Mission;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

/// <summary>Mission plans on the simulated backend: what the validator refuses, how roles expand,
/// and that the engine runs the operator's plan at the right moments - checked against the
/// backend's own UAV state, not the messages.</summary>
public sealed class MissionPlanTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "uavops-mission-plans-" + Guid.NewGuid().ToString("N"));
    private readonly SimulatedUavOperationService _moav = new();
    private readonly RecordingOperatorNotifier _notifier = new();
    private readonly ServiceProvider _services;
    private readonly MissionEngine _engine;
    private readonly MissionEventService _missionEvents;

    public MissionPlanTests()
    {
        var zones = new SqliteAoiZoneStore(Path.Combine(_directory, "aoi.db"));
        var options = new MissionOptions();
        var services = new ServiceCollection();
        services.AddSingleton<IOperationService>(_moav);
        services.AddSingleton<IAoiZoneStore>(zones);
        services.AddSingleton<IRouteStore>(new InMemoryRouteStore());
        services.AddSingleton(options);
        services.AddSingleton(new DetectionPointRegistry());
        services.AddSingleton<IOperatorNotifier>(_notifier);
        services.AddSingleton(sp => new MissionEventService(_notifier, sp.GetRequiredService<DetectionPointRegistry>(), options,
            NullLogger<MissionEventService>.Instance));
        services.AddSingleton(McpToolsConfigLoader.Load(Path.Combine(RepoRoot(), "src", "UavOps.Agent.McpMoav", "ToolsConfig.yaml")));
        services.AddSingleton<StepExecutor>();
        services.AddSingleton<MissionPlanValidator>();
        services.AddSingleton<MissionEngine>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _services = services.BuildServiceProvider();
        _engine = _services.GetRequiredService<MissionEngine>();
        _missionEvents = _services.GetRequiredService<MissionEventService>();
    }

    public void Dispose()
    {
        _services.Dispose();
        TempDirectory.DeleteSqliteFolder(_directory);
    }

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static MissionStepInput Step(string when, string tool, object args) =>
        new(when, tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)));

    /// <summary>The operator's example: "Send 997 and 998 to search and track for a red car in ZoneA,
    /// when the car is found return the other uav home and keep tracking the red car".</summary>
    private static MissionStepInput[] SearchAndTrackPlan() =>
    [
        Step("start", "PrepareAoiSearch", new { tailNumbers = new[] { "997", "998" }, zoneName = "ZoneA", targetDescription = "red car", track = true }),
        Step("after 1", "StartMission", new { tailNumber = "{all}" }),
        Step("target.found", "ReturnToLaunch", new { tailNumber = "{others}" })
    ];

    private async Task<string> ModeOf(string tail) => ((TelemetrySnapshot)(await _moav.GetTelemetry(tail, CancellationToken.None)).Value!).Mode;

    private static async Task Eventually(Func<Task<bool>> condition, string because)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        (await condition()).Should().BeTrue(because);
    }

    // ----- Validator -----

    [Fact]
    public async Task Validator_AcceptsTheOperatorsPlan_AndCollectsItsUavs()
    {
        var (plan, errors) = await _engine.CreateAsync(SearchAndTrackPlan(), CancellationToken.None);

        errors.Should().BeEmpty();
        plan!.Uavs.Should().BeEquivalentTo("997", "998");
        plan.Tracks.Should().BeTrue();
        plan.Status.Should().Be(PlanStatus.Created);
    }

    [Fact]
    public async Task Validator_RefusesEachProblem_NamingItsStep()
    {
        var (plan, errors) = await _engine.CreateAsync(
        [
            Step("start", "PrepareAoiSearch", new { tailNumbers = new[] { "997" }, zoneName = "ZoneZ", targetDescription = "red car" }),
            Step("after 3", "StartMission", new { tailNumber = "997" }),
            Step("when it rains", "ReturnToLaunch", new { tailNumber = "997" }),
            Step("start", "GetTelemetry", new { tailNumber = "997" }),
            Step("start", "SetSpeed", new { tailNumber = "997", speed = 200 }),
            Step("start", "ReturnToLaunch", new { tailNumber = "{others}" }),
            Step("start", "ReturnToLaunch", new { tailNumber = "123" })
        ], CancellationToken.None);

        plan.Should().BeNull("a plan with any problem is never stored");
        errors.Should().Contain(e => e.StartsWith("Step 1:") && e.Contains("ZoneZ"));
        errors.Should().Contain(e => e.StartsWith("Step 2:") && e.Contains("earlier step"));
        errors.Should().Contain(e => e.StartsWith("Step 3:") && e.Contains("isn't a trigger"));
        errors.Should().Contain(e => e.StartsWith("Step 4:") && e.Contains("isn't a tool a step can run"), "read-only tools aren't actions");
        errors.Should().Contain(e => e.StartsWith("Step 5:") && e.Contains("no argument 'speed'"));
        errors.Should().Contain(e => e.StartsWith("Step 5:") && e.Contains("needs 'speedKts'"));
        errors.Should().Contain(e => e.StartsWith("Step 6:") && e.Contains("once the target was found"));
        errors.Should().Contain(e => e.StartsWith("Step 7:") && e.Contains("isn't a known UAV"));
    }

    [Fact]
    public async Task Validator_AllowsFinderRoles_InAStepThatFollowsATargetStep()
    {
        var (_, errors) = await _engine.CreateAsync(
        [
            Step("start", "StartMission", new { tailNumber = "997" }),
            Step("target.found", "SetSpeed", new { tailNumber = "{finder}", speedKts = 120 }),
            Step("after 2", "ReturnToLaunch", new { tailNumber = "{others}" })
        ], CancellationToken.None);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validator_RefusesAPlanThatNeverStarts()
    {
        var (_, errors) = await _engine.CreateAsync([Step("target.found", "ReturnToLaunch", new { tailNumber = "997" })], CancellationToken.None);

        errors.Should().Contain(e => e.Contains("never begin"));
    }

    // ----- Roles and descriptions -----

    [Fact]
    public void ExpandTails_FillsInRoles()
    {
        string[] all = ["997", "998", "999"];
        StepExecutor.ExpandTails(J("{all}"), all, null).Should().Equal("997", "998", "999");
        StepExecutor.ExpandTails(J("{finder}"), all, "998").Should().Equal("998");
        StepExecutor.ExpandTails(J("{others}"), all, "998").Should().Equal("997", "999");
        StepExecutor.ExpandTails(J("997, 999"), all, null).Should().Equal("997", "999");
        StepExecutor.ExpandTails(J(new[] { "{finder}", "999" }), all, "997").Should().Equal("997", "999");
        var beforeFound = () => StepExecutor.ExpandTails(J("{others}"), all, null);
        beforeFound.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Describer_ReadsTheStepsInPlainWords()
    {
        var (plan, _) = await _engine.CreateAsync(SearchAndTrackPlan(), CancellationToken.None);

        MissionDescriber.Describe(plan!).Should().Be(
            "1. At start: 997 and 998 search ZoneA for a red car and track it once found (the zone split between them, routes uploaded).\n" +
            "2. After step 1: all the mission's UAVs start flying the search.\n" +
            "3. When the target is found: the other UAVs return home.");
    }

    // ----- Engine -----

    [Fact]
    public async Task NothingRuns_UntilTheOperatorStartsThePlan()
    {
        await _engine.CreateAsync(SearchAndTrackPlan(), CancellationToken.None);
        await Task.Delay(200);

        (await ModeOf("997")).Should().NotBe("Searching");
        _notifier.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task OperatorsPlan_SearchesWithBoth_ThenSendsTheOtherHomeWhenOneLocksOn()
    {
        var (plan, _) = await _engine.CreateAsync(SearchAndTrackPlan(), CancellationToken.None);

        _engine.Start(plan!).Should().BeEmpty();
        await Eventually(async () => await ModeOf("997") == "Searching" && await ModeOf("998") == "Searching", "steps 1 and 2 start both searches");
        plan!.Steps[2].State.Should().Be(StepState.Waiting);

        var mission998 = _missionEvents.MissionOf("998")!;
        _engine.OnMissionEvent(new MissionEventReport("998", mission998, "ZoneA", MissionEventKinds.Tracking));

        await Eventually(async () => await ModeOf("997") == "ReturningToLaunch", "997 is the other UAV");
        plan.Finder.Should().Be("998");
        (await ModeOf("998")).Should().Be("Searching", "the finder keeps on the target");
        plan.Status.Should().Be(PlanStatus.Running, "a tracking plan keeps its one-tracker rule until cancelled");
        // Short, by tail number, the why only for a target event; the voice says only what was done.
        _notifier.Posted.Should().Contain(p => p.Message == "Step 3 (998 locked on the target): 997 returning home.");
        _notifier.Posted.Should().Contain(p => p.Message == "Step 2: 997 and 998 searching.");
    }

    [Fact]
    public async Task ASecondUavLockingOn_IsStopped_AndTheFinderKeepsTracking()
    {
        var (plan, _) = await _engine.CreateAsync(
        [
            Step("start", "PrepareAoiSearch", new { tailNumbers = new[] { "997", "998" }, zoneName = "ZoneA", targetDescription = "red car", track = true }),
            Step("after 1", "StartMission", new { tailNumber = "{all}" })
        ], CancellationToken.None);
        _engine.Start(plan!);
        await Eventually(async () => await ModeOf("997") == "Searching" && await ModeOf("998") == "Searching", "both search");

        _engine.OnMissionEvent(new MissionEventReport("998", _missionEvents.MissionOf("998")!, "ZoneA", MissionEventKinds.Tracking));
        _engine.OnMissionEvent(new MissionEventReport("997", _missionEvents.MissionOf("997")!, "ZoneA", MissionEventKinds.Tracking));

        await Eventually(async () => await ModeOf("997") == "Orbiting", "997 locked on second and is stopped");
        (await ModeOf("998")).Should().Be("Searching");
        await Eventually(() => Task.FromResult(_notifier.Posted.Any(p => p.Message.Contains("998 is already tracking it"))), "the operator is told");
    }

    [Fact]
    public async Task AnAfterStep_WaitsForItsStep_AndATimedStepForItsTime()
    {
        var (plan, _) = await _engine.CreateAsync(
        [
            Step("start", "SetSpeed", new { tailNumber = "997", speedKts = 150 }),
            Step("after 1", "SetAltitude", new { tailNumber = "997", altitudeFt = 5000 }),
            Step("time 1", "ReturnToLaunch", new { tailNumber = "997" })
        ], CancellationToken.None);
        _engine.Start(plan!);

        await Eventually(() => Task.FromResult(plan!.Steps[1].State == StepState.Done), "step 2 follows step 1");
        plan!.Steps[2].State.Should().Be(StepState.Waiting, "a second hasn't passed yet");
        await Eventually(async () => await ModeOf("997") == "ReturningToLaunch", "the timed step runs after its second");
    }

    [Fact]
    public async Task CancelledPlan_RunsNoMoreSteps()
    {
        var (plan, _) = await _engine.CreateAsync(
        [
            Step("start", "SetSpeed", new { tailNumber = "997", speedKts = 150 }),
            Step("target.found", "ReturnToLaunch", new { tailNumber = "{all}" })
        ], CancellationToken.None);
        _engine.Start(plan!);
        await Eventually(() => Task.FromResult(plan!.Steps[0].State == StepState.Done), "the start step runs");

        _engine.Cancel(plan!);
        _engine.OnMissionEvent(new MissionEventReport("997", "M1", "ZoneA", MissionEventKinds.Tracking));
        await Task.Delay(200);

        plan!.Steps[1].State.Should().Be(StepState.Skipped);
        (await ModeOf("997")).Should().NotBe("ReturningToLaunch");
        _engine.Start(plan).Should().NotBeEmpty("a cancelled plan can't be started again");
    }

    [Fact]
    public async Task Tools_CreateShowsTheSteps_AndStartWithoutAnIdStartsTheLatestPlan()
    {
        var created = await MoavTools.CreateMissionPlan(_engine, SearchAndTrackPlan(), CancellationToken.None);
        var json = JsonDocument.Parse(created).RootElement;
        json.GetProperty("planId").GetString().Should().Be("P1");
        json.GetProperty("steps").GetString().Should().Contain("3. When the target is found: the other UAVs return home.");

        MoavTools.StartMissionPlan(_engine).Should().Contain("\"started\":true");
        await Eventually(async () => await ModeOf("998") == "Searching", "the plan's steps start the search");

        MoavTools.StartMissionPlan(_engine).Should().StartWith("Error", "it's already running");
        MoavTools.GetMissionPlan(_engine).Should().Contain("\"status\":\"Running\"");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UavOps.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}

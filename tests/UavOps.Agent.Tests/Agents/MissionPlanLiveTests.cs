using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// A mission with a "when" through the real model, word for word as the operator gave it: it must
/// become ONE mission plan whose steps are the search with both UAVs (tracking), starting it, and
/// sending the other UAV home when the target is found - and nothing may fly yet. Then "start the
/// mission" must start the plan (StartMissionPlan), not the UAVs directly: both UAVs end up searching
/// and the plan is running, with its target.found step still waiting. Checked on the plan and the
/// backend's real mission state, not the reply.
/// </summary>
public class MissionPlanLiveTests(ITestOutputHelper output)
{
    static MissionPlanLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    private const string Mission =
        "Send 997 and 998 to search and track for a red car in ZoneA, when the car is found return the other uav home and keep tracking the red car";

    [Fact]
    [Trait("Category", "Live")]
    public async Task SearchAndTrackWithAWhen_BecomesAPlan_ThenStartRunsIt()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;
        string[] team = ["997", "998"];

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[MissionPlan] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, mcpClients, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;

            var (planSummary, _) = await orchestrator.HandleAsync(Mission, $"mission-plan-{i}-1", CancellationToken.None);
            output.WriteLine($"[plan] {planSummary}");
            var created = Plan(await mcpClients.CallToolTextAsync("GetMissionPlan", new()));
            var afterPlan = await TeamAoiSearchLiveTests.TeamStatusAsync(mcpClients, team);

            var (startSummary, _) = await orchestrator.HandleAsync("start the mission", $"mission-plan-{i}-2", CancellationToken.None);
            output.WriteLine($"[start] {startSummary}");
            // The plan's steps run off the tool call: give them a moment.
            List<UavOps.Agent.Contracts.MissionStatus> afterStart = [];
            for (var wait = 0; wait < 20; wait++)
            {
                afterStart = await TeamAoiSearchLiveTests.TeamStatusAsync(mcpClients, team);
                if (afterStart.All(s => s.Mode == "Searching"))
                    break;
                await Task.Delay(250);
            }
            var started = Plan(await mcpClients.CallToolTextAsync("GetMissionPlan", new()));

            var planRight = created is { Status: "Created" } &&
                            created.Steps.Any(s => s.Text.Contains("997 and 998 search ZoneA for a red car and track it")) &&
                            created.Steps.Any(s => s.Text.StartsWith("When the target is found") && s.Text.Contains("the other UAVs return home"));
            var nothingFlew = afterPlan.All(s => s.Mode != "Searching");
            var startedRight = started is { Status: "Running" } && afterStart.All(s => s.Mode == "Searching") &&
                               started.Steps.Single(s => s.Text.StartsWith("When the target is found")).State == "Waiting";

            if (planRight && nothingFlew && startedRight)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (one plan, shown, then started by its own steps)");
            }
            else
            {
                LiveTestSupport.LiveLog(output,
                    $"  => MISMATCH (plan: {Describe(created)}; nothing flew: {nothingFlew}; after start: {Describe(started)}, " +
                    $"{string.Join(" | ", afterStart.Select(s => s.Mode))}; reply: {planSummary} / {startSummary})");
            }
        }

        LiveTestSupport.LiveLog(output, $"[MissionPlan] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats);
    }

    private sealed record PlanView(string Status, List<StepView> Steps);
    private sealed record StepView(int Number, string Text, string State);

    private static PlanView? Plan(string json) =>
        json.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<PlanView>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;

    private static string Describe(PlanView? plan) =>
        plan is null ? "none" : $"{plan.Status}: " + string.Join(" / ", plan.Steps.Select(s => $"{s.Number}. {s.Text} [{s.State}]"));
}

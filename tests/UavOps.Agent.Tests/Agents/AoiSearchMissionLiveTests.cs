using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// The AOI search mission end to end through the real model: "Enter AOI zone ZoneA and search
/// for white van" names no UAV, so the tail-number prompt asks which one (answered "997"). That
/// turn must upload a route and set the search target without starting; "start the mission" then
/// must actually start it with no further approval prompt: the operator saying "start" is the
/// approval. Checked against the backend's real mission status after each step. The one text
/// check: the prepare reply must not ask "Start the mission?" itself, which would make the
/// operator approve twice.
/// </summary>
public class AoiSearchMissionLiveTests(ITestOutputHelper output)
{
    static AoiSearchMissionLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task EnterZoneAndSearch_PreparesWithoutStarting_ThenStartMissionStarts()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[AoiSearchMission] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, mcpClients, _, promptGate) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;
            var correlationPrefix = $"aoi-search-{i}";

            var prepare = orchestrator.HandleAsync("Enter AOI zone ZoneA and search for white van", $"{correlationPrefix}-1", CancellationToken.None);
            await LiveTestSupport.AnswerOperatorPromptsAsync(promptGate, prepare, "997");
            var (prepareSummary, _) = await prepare;
            output.WriteLine($"[prepare] {prepareSummary}");
            var afterPrepare = await MissionStatusAsync(mcpClients, "997");
            output.WriteLine($"997 after prepare: {afterPrepare}");

            var start = orchestrator.HandleAsync("start the mission", $"{correlationPrefix}-2", CancellationToken.None);
            var (startSummary, _) = await start;
            output.WriteLine($"[start] {startSummary}");
            var afterStart = await MissionStatusAsync(mcpClients, "997");
            output.WriteLine($"997 after start: {afterStart}");

            var prepared = afterPrepare.WaypointCount > 0 &&
                           afterPrepare.SearchPrompt?.Contains("white van", StringComparison.OrdinalIgnoreCase) == true &&
                           afterPrepare.Mode != "Searching";
            var askedToStart = Regex.IsMatch(prepareSummary, @"\bstart\b[^.!?]*\?", RegexOptions.IgnoreCase);
            var started = afterStart.Mode == "Searching";

            if (prepared && !askedToStart && started)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (prepared without starting, then started)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (after prepare: {afterPrepare}; asked to start: {askedToStart}; after start: {afterStart})");
            }
        }

        LiveTestSupport.LiveLog(output, $"[AoiSearchMission] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats);
    }

    private static async Task<MissionStatus> MissionStatusAsync(LiveTestSupport.McpClientGroup mcpClients, string tailNumber)
    {
        var json = await mcpClients.CallToolTextAsync("GetMissionStatus", new() { ["tailNumber"] = tailNumber });
        return JsonSerializer.Deserialize<MissionStatus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Could not parse GetMissionStatus result: {json}");
    }
}

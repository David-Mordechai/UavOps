using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// A team search through the real model: "send 997 and 998 to search for a red car in ZoneA" must
/// be ONE PrepareAoiSearch call with both UAVs, so the zone is split between them. Checked on the
/// backend's real mission state: both UAVs have a route and the red-car target, neither is flying
/// it yet, and both routes carry the same team id - two separate one-UAV calls would each plan the
/// whole zone under their own id. Both fly at 4000 ft, so the reply must tell the operator so (the
/// first demo's reply dropped it). Then "start the mission" must start both.
/// </summary>
public class TeamAoiSearchLiveTests(ITestOutputHelper output)
{
    static TeamAoiSearchLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task SendTwoUavsToSearch_SplitsTheZoneInOneCall_ThenStartStartsBoth()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;
        string[] team = ["997", "998"];

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[TeamAoiSearch] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, mcpClients, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;
            var correlationPrefix = $"team-search-{i}";

            var (prepareSummary, _) = await orchestrator.HandleAsync("send 997 and 998 to search for a red car in ZoneA", $"{correlationPrefix}-1", CancellationToken.None);
            output.WriteLine($"[prepare] {prepareSummary}");
            var afterPrepare = await TeamStatusAsync(mcpClients, team);

            var (startSummary, _) = await orchestrator.HandleAsync("start the mission", $"{correlationPrefix}-2", CancellationToken.None);
            output.WriteLine($"[start] {startSummary}");
            var afterStart = await TeamStatusAsync(mcpClients, team);

            var prepared = TeamPrepared(afterPrepare, "red car");
            var askedToStart = Regex.IsMatch(prepareSummary, @"\bstart\b[^.!?]*\?", RegexOptions.IgnoreCase);
            var toldAltitude = prepareSummary.Contains("4000", StringComparison.Ordinal);
            var started = afterStart.All(s => s.Mode == "Searching");

            if (prepared && !askedToStart && toldAltitude && started)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (one team search, altitude told, then both started)");
            }
            else
            {
                LiveTestSupport.LiveLog(output,
                    $"  => MISMATCH (after prepare: {string.Join(" | ", afterPrepare)}; asked to start: {askedToStart}; " +
                    $"told altitude: {toldAltitude}; after start: {string.Join(" | ", afterStart)}; reply: {prepareSummary})");
            }
        }

        LiveTestSupport.LiveLog(output, $"[TeamAoiSearch] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats);
    }

    /// <summary>Every member has a route and the target, none is flying it, and all routes end in
    /// the same team id (route ids are "{zone}-{tail}-{teamId}").</summary>
    internal static bool TeamPrepared(IReadOnlyList<MissionStatus> statuses, string target) =>
        statuses.All(s => s.WaypointCount > 0 &&
                          s.SearchPrompt?.Contains(target, StringComparison.OrdinalIgnoreCase) == true &&
                          s.Mode != "Searching" &&
                          s.ActiveMissionId is { Length: > 6 }) &&
        statuses.Select(s => s.ActiveMissionId![^6..]).Distinct().Count() == 1;

    internal static async Task<List<MissionStatus>> TeamStatusAsync(LiveTestSupport.McpClientGroup mcpClients, IEnumerable<string> tails)
    {
        var statuses = new List<MissionStatus>();
        foreach (var tail in tails)
        {
            var json = await mcpClients.CallToolTextAsync("GetMissionStatus", new() { ["tailNumber"] = tail });
            statuses.Add(JsonSerializer.Deserialize<MissionStatus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"Could not parse GetMissionStatus result: {json}"));
        }
        return statuses;
    }
}

/// <summary>"Search ZoneA for a red car with all UAVs": there is no 'ALL' for a search, so the model
/// must list the fleet itself and pass every tail number in one PrepareAoiSearch call. Since
/// 2026-10-01 all three start at the base by ZoneA, so all three must be one team. (Leaving out a
/// far UAV is covered by MoavMissionToolsTests.)</summary>
public class TeamAoiSearchAllUavsLiveTests(ITestOutputHelper output)
{
    static TeamAoiSearchAllUavsLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task SearchWithAllUavs_ListsTheFleet_AndMakesOneTeamOfAllThree()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[TeamAoiSearchAll] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, mcpClients, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;

            var (summary, _) = await orchestrator.HandleAsync("search ZoneA for a red car with all UAVs", $"team-search-all-{i}", CancellationToken.None);
            output.WriteLine($"[prepare] {summary}");
            var team = await TeamAoiSearchLiveTests.TeamStatusAsync(mcpClients, ["997", "998", "999"]);

            if (TeamAoiSearchLiveTests.TeamPrepared(team, "red car"))
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (997+998+999 one team)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (team: {string.Join(" | ", team)}; reply: {summary})");
            }
        }

        LiveTestSupport.LiveLog(output, $"[TeamAoiSearchAll] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats);
    }
}

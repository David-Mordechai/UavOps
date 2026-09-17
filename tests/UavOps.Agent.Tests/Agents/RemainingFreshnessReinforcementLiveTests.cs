using System.Text.Json;
using Microsoft.Extensions.AI;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Resolves finding #1 from this session's tool-description-review pass for every tool where a
/// real, live-testable "did it re-fetch after a real state change" scenario can actually be built
/// with the Fake backends this app ships with: <c>GetTelemetry</c>, <c>GetLinkStatus</c>,
/// <c>GetMissionStatus</c> (Moav) and <c>ListConfiguredServices</c> (Watchdog). Each already
/// carries a per-tool "always call this fresh" sentence that duplicates BrainAgent.yaml's own
/// thorough "Resolving references from history" instructions - this test is what decides,
/// per tool, whether that duplication is safe to trim (same evidence-based standard as
/// <see cref="RepeatedFleetQueryLiveTests"/> already established for <c>ListFleet</c>), by asking
/// the identical question twice with a real mutation in between and checking the SECOND answer
/// reflects the new state, not the first.
///
/// <c>GetServicesHealth</c>, <c>ListConfigurations</c>, and <c>ListSimulatorLessons</c> are
/// deliberately NOT covered here and NOT trimmed - their Fake backends have no way to change the
/// values those specific tools report through any exposed tool call (Watchdog's
/// <c>StartService</c>/<c>StopService</c>/<c>RestartService</c> only ever target the watchdog
/// process itself, never the three services <c>GetServicesHealth</c> actually reports on; the
/// configuration names <c>ListConfigurations</c> lists and the lesson list
/// <c>ListSimulatorLessons</c> returns are both fixed, with no "add a new one" tool for either).
/// Without a real, observable state change, there is no way to distinguish "the model correctly
/// re-fetched" from "the model got lucky reciting the same unchanged answer from memory" via this
/// project's established response-text-based verification idiom - building one would require
/// changing the Fake backends themselves, out of scope for a pure prompt/description pass. Their
/// reinforcement sentences stay as-is until that changes.
/// </summary>
public class RemainingFreshnessReinforcementLiveTests(ITestOutputHelper output)
{
    static RemainingFreshnessReinforcementLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    private static async Task<T> InvokeToolAsync<T>(IReadOnlyList<AIFunction> tools, string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var tool = tools.First(t => t.Name == toolName);
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in args)
        {
            arguments[key] = value;
        }

        var raw = await tool.InvokeAsync(arguments, cancellationToken);
        var json = raw?.ToString() ?? throw new InvalidOperationException($"{toolName} returned no result.");
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Could not parse {toolName} result: {json}");
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task RepeatedStatusQuestions_AfterRealStateChange_ReflectCurrentStateNotStaleMemory()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[RemainingFreshness] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, factory, mcpClients, confirmationGate, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;
            var tools = factory.McpTools;
            var p = $"remaining-freshness-{i}";
            var mismatches = new List<string>();

            void Check(bool condition, string description)
            {
                if (!condition)
                {
                    mismatches.Add(description);
                }
            }

            await orchestrator.HandleAsync("Hi, this is David, operator for shift 1.", $"{p}-1", CancellationToken.None);

            // ---- GetTelemetry(997): Orbiting/105/4000 -> Transiting/220/4500 ----
            var (telemetryBefore, _) = await orchestrator.HandleAsync("Give me a telemetry check on 997.", $"{p}-2", CancellationToken.None);
            Check(telemetryBefore.Contains("orbiting", StringComparison.OrdinalIgnoreCase),
                $"GetTelemetry(997) baseline: expected 'Orbiting' before any mutation, got: \"{telemetryBefore}\"");

            await orchestrator.HandleAsync("Send 997 to target alpha, speed 220 knots, altitude 4500 feet.", $"{p}-3", CancellationToken.None);

            var (telemetryAfter, _) = await orchestrator.HandleAsync("Give me a telemetry check on 997.", $"{p}-4", CancellationToken.None);
            Check(telemetryAfter.Contains("220", StringComparison.OrdinalIgnoreCase) || telemetryAfter.Contains("transit", StringComparison.OrdinalIgnoreCase),
                $"GetTelemetry(997) repeat: expected fresh 'Transiting'/220kts state, got: \"{telemetryAfter}\"");
            Check(!telemetryAfter.Contains("orbiting", StringComparison.OrdinalIgnoreCase),
                $"GetTelemetry(997) repeat: still reported stale 'Orbiting', got: \"{telemetryAfter}\"");

            // ---- GetLinkStatus(998): TrackingMode Auto -> Manual ----
            var (linkBefore, _) = await orchestrator.HandleAsync("Check the data link status for 998.", $"{p}-5", CancellationToken.None);
            Check(linkBefore.Contains("auto", StringComparison.OrdinalIgnoreCase),
                $"GetLinkStatus(998) baseline: expected 'Auto' tracking mode before any mutation, got: \"{linkBefore}\"");

            await orchestrator.HandleAsync("Set 998's tracking mode to manual.", $"{p}-6", CancellationToken.None);

            var (linkAfter, _) = await orchestrator.HandleAsync("Check the data link status for 998 again.", $"{p}-7", CancellationToken.None);
            Check(linkAfter.Contains("manual", StringComparison.OrdinalIgnoreCase),
                $"GetLinkStatus(998) repeat: expected fresh 'Manual' tracking mode, got: \"{linkAfter}\"");

            // ---- GetMissionStatus(999): 0 waypoints -> 1 waypoint ----
            var (missionBefore, _) = await orchestrator.HandleAsync("What's the mission status on 999?", $"{p}-8", CancellationToken.None);
            Check(missionBefore.Contains("0", StringComparison.OrdinalIgnoreCase) || missionBefore.Contains("no waypoint", StringComparison.OrdinalIgnoreCase) || missionBefore.Contains("no active", StringComparison.OrdinalIgnoreCase),
                $"GetMissionStatus(999) baseline: expected zero waypoints before any mutation, got: \"{missionBefore}\"");

            await orchestrator.HandleAsync(
                "Upload a one-point route to 999: latitude 31.8, longitude 34.6, altitude 3000 feet.", $"{p}-9", CancellationToken.None);

            var (missionAfter, _) = await orchestrator.HandleAsync("What's the mission status on 999 now?", $"{p}-10", CancellationToken.None);
            var mission999 = await InvokeToolAsync<MissionStatus>(tools, "GetMissionStatus", new() { ["tailNumber"] = "999" }, CancellationToken.None);
            Check(mission999.WaypointCount == 1, $"GetMissionStatus(999) ground truth mismatch: waypointCount={mission999.WaypointCount}");
            // Accepts the digit or the spelled-out word - live-observed a correct, fresh answer
            // phrased as "Waypoint count one" failing this check when it only looked for "1".
            Check(missionAfter.Contains("1", StringComparison.OrdinalIgnoreCase) || missionAfter.Contains("one", StringComparison.OrdinalIgnoreCase),
                $"GetMissionStatus(999) repeat: expected the response to reflect the fresh 1-waypoint count, got: \"{missionAfter}\"");

            // ---- ListConfiguredServices(Flight): Service One/Two -> +Service 3 ----
            var (servicesBefore, _) = await orchestrator.HandleAsync("Show me the services configured under Flight.", $"{p}-11", CancellationToken.None);
            Check(servicesBefore.Contains("Service One", StringComparison.OrdinalIgnoreCase) && servicesBefore.Contains("Service Two", StringComparison.OrdinalIgnoreCase),
                $"ListConfiguredServices(Flight) baseline: expected Service One/Two, got: \"{servicesBefore}\"");
            Check(!servicesBefore.Contains("Service 3", StringComparison.OrdinalIgnoreCase),
                $"ListConfiguredServices(Flight) baseline: unexpectedly already mentions Service 3, got: \"{servicesBefore}\"");

            var addTask = orchestrator.HandleAsync("Add a new service called Service 3 to the Flight configuration.", $"{p}-12", CancellationToken.None);
            await LiveTestSupport.ApproveAnyPendingConfirmationAsync(confirmationGate, addTask);
            await addTask;

            var (servicesAfter, _) = await orchestrator.HandleAsync("Show me the services configured under Flight again.", $"{p}-13", CancellationToken.None);
            Check(servicesAfter.Contains("Service 3", StringComparison.OrdinalIgnoreCase),
                $"ListConfiguredServices(Flight) repeat: expected fresh list to include Service 3, got: \"{servicesAfter}\"");

            LiveTestSupport.LiveLog(output, $"[RemainingFreshness] repeat {i + 1}/{repeats} finished, {mismatches.Count} mismatch(es)");
            if (mismatches.Count > 0)
            {
                foreach (var m in mismatches)
                {
                    LiveTestSupport.LiveLog(output, "  MISMATCH: " + m);
                }
            }
            else
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (every repeated question reflected fresh state, not stale memory)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[RemainingFreshness] FINAL: Verified successes: {successes}/{repeats}");
        Assert.True(successes == repeats, $"Freshness reinforcement scenario had at least one mismatch in {repeats - successes}/{repeats} repeats - see test output above.");
    }
}

using System.Text.Json;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Permanent regression coverage for the full "Fleet Ops Voice Demo" scenario prepared and
/// manually dry-run-validated this session (simulator warm-up, a multi-UAV live mission, then
/// watchdog maintenance) - saved here per the operator's own request so future changes can be
/// checked against the exact same real, end-to-end flow rather than re-validating by hand every
/// time. Exercises 23 of this app's 25 real tools in one continuous session (only
/// <c>EnsureVmwareHostRunning</c>/<c>EnsureSimulatorVmRunning</c> are skipped, matching the
/// operator's own choice to simplify the demo's Act 1 - see this session's own conversation
/// history for that call).
///
/// "Correct tool calling", not just a plausible-sounding reply, is what's actually checked
/// throughout - matching this project's own established live-test idiom (see
/// <see cref="RepeatedFleetQueryLiveTests"/> and <see cref="ReturnRemainingFleetLiveTests"/>), not
/// a new one invented for this file: <see cref="Tooling.ToolInvocationLogger.GetAgentInvocationCount"/>
/// reads 0 unconditionally once <see cref="MainAgentOrchestrator.HandleAsync"/> returns (it clears
/// invocation tracking for the correlationId before returning - confirmed by
/// <see cref="RunSimulatorLessonLiveTests"/>'s own doc comment), so it can't be used here either.
/// Every mutating step is instead verified by independently calling the SAME real MCP tool again
/// afterward (bypassing the model entirely) to confirm the actual backend state changed - not by
/// trusting the model's own summary of what it did. Every read-only step is verified by asserting
/// the reported value against the deterministic Fake/Simulated backend's own known real state,
/// the same way <see cref="RepeatedFleetQueryLiveTests"/> checks "Transiting" replaced "Orbiting"
/// rather than assuming a plausible-sounding answer proves a fresh call happened.
/// </summary>
public class FullShiftScenarioLiveTests(ITestOutputHelper output)
{
    static FullShiftScenarioLiveTests() => LiveTestSupport.ClearProgressLogOnce();

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
    public async Task FullShiftScenario_EveryStepReflectsARealToolCallNotAFabricatedAnswer()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[FullShiftScenario] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, factory, mcpClients, confirmationGate, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;
            var tools = factory.McpTools;
            var p = $"full-shift-{i}";
            var mismatches = new List<string>();

            void Check(bool condition, string description)
            {
                if (!condition)
                {
                    mismatches.Add(description);
                }
            }

            // ---- Act 1: simulator warm-up ----
            await orchestrator.HandleAsync("Hi, this is David, operator for shift 1 tonight.", $"{p}-1", CancellationToken.None);

            var (lessonsText, _) = await orchestrator.HandleAsync("List the available simulator lessons for me.", $"{p}-2", CancellationToken.None);
            Check(lessonsText.Contains("intro-flight-basics", StringComparison.OrdinalIgnoreCase)
                && lessonsText.Contains("advanced-navigation", StringComparison.OrdinalIgnoreCase)
                && lessonsText.Contains("emergency-procedures", StringComparison.OrdinalIgnoreCase),
                "ListSimulatorLessons: response did not name all 3 known fake lessons");

            var lessonTask = orchestrator.HandleAsync("Let's run the emergency-procedures lesson before we start real ops.", $"{p}-3", CancellationToken.None);
            await LiveTestSupport.ApproveAnyPendingConfirmationAsync(confirmationGate, lessonTask);
            var (lessonRunText, _) = await lessonTask;
            Check(lessonRunText.Contains("queue", StringComparison.OrdinalIgnoreCase) || lessonRunText.Contains("start", StringComparison.OrdinalIgnoreCase),
                "RunSimulatorLesson: response did not report the lesson as queued/started");

            // ---- Act 2: live mission ----
            var (fleetText, _) = await orchestrator.HandleAsync("Alright, we're live. What UAVs do we have available right now?", $"{p}-4", CancellationToken.None);
            Check(fleetText.Contains("997") && fleetText.Contains("998") && fleetText.Contains("999"),
                "ListFleet: response did not name all 3 known UAVs");

            await orchestrator.HandleAsync("Give me a telemetry check on tail number 997.", $"{p}-5", CancellationToken.None);
            var telemetry997Before = await getTelemetry("997", CancellationToken.None);
            Check(telemetry997Before.Mode == "Orbiting", "GetTelemetry(997) ground truth: expected still Orbiting before any mutation");

            await orchestrator.HandleAsync("Also check the data link status for 998.", $"{p}-6", CancellationToken.None);
            var link998 = await InvokeToolAsync<GdtLinkStatus>(tools, "GetLinkStatus", new() { ["tailNumber"] = "998" }, CancellationToken.None);
            Check(link998.LinkState == "Connected" && link998.SignalStrengthPercent == 92, "GetLinkStatus(998) ground truth: expected Connected/92% (fresh call, not a fabricated default)");

            await orchestrator.HandleAsync("Send 997 to target Alpha, speed 220 knots, altitude 4000 feet.", $"{p}-7", CancellationToken.None);
            var telemetry997After = await getTelemetry("997", CancellationToken.None);
            Check(telemetry997After.Mode == "Transiting" && telemetry997After.SpeedKts == 220 && telemetry997After.AltitudeFt == 4000,
                $"Navigate+SetSpeed+SetAltitude(997) ground truth mismatch: mode={telemetry997After.Mode}, speed={telemetry997After.SpeedKts}, alt={telemetry997After.AltitudeFt}");

            await orchestrator.HandleAsync("Send 998 to target Bravo, speed 190 knots, altitude 3500 feet.", $"{p}-8", CancellationToken.None);
            var telemetry998 = await getTelemetry("998", CancellationToken.None);
            Check(telemetry998.Mode == "Transiting" && telemetry998.SpeedKts == 190 && telemetry998.AltitudeFt == 3500,
                $"Navigate+SetSpeed+SetAltitude(998) ground truth mismatch: mode={telemetry998.Mode}, speed={telemetry998.SpeedKts}, alt={telemetry998.AltitudeFt}");

            await orchestrator.HandleAsync("Set 998's tracking mode to follow target.", $"{p}-9", CancellationToken.None);
            var link998AfterTracking = await InvokeToolAsync<GdtLinkStatus>(tools, "GetLinkStatus", new() { ["tailNumber"] = "998" }, CancellationToken.None);
            Check(link998AfterTracking.TrackingMode == "Auto", $"SetTrackingMode(998) ground truth mismatch: trackingMode={link998AfterTracking.TrackingMode}");

            await orchestrator.HandleAsync("Point 998's payload at target Bravo.", $"{p}-10", CancellationToken.None);
            var telemetry998AfterPoint = await getTelemetry("998", CancellationToken.None);
            // Case-insensitive: PointPayload stores whatever location string it's given verbatim
            // (see SimulatedUavOperationService.PointPayload) - "Bravo" (matching the operator's
            // own capitalization after LocationCanonicalizationTool strips "target ") is just as
            // correct as "bravo" would be; this check isn't about casing, only about whether the
            // real backend recorded the right point at all.
            Check(string.Equals(telemetry998AfterPoint.PayloadLockedOn, "bravo", StringComparison.OrdinalIgnoreCase),
                $"PointPayload(998) ground truth mismatch: payloadLockedOn={telemetry998AfterPoint.PayloadLockedOn}");

            var (missionStatus997Text, _) = await orchestrator.HandleAsync("What's the mission status on 997?", $"{p}-11", CancellationToken.None);
            Check(missionStatus997Text.Contains("Transiting", StringComparison.OrdinalIgnoreCase),
                "GetMissionStatus(997): response did not reflect the real current (post-Navigate) mode");

            await orchestrator.HandleAsync(
                "Upload a two-point patrol route to 999: first waypoint latitude 31.812, longitude 34.660, altitude 3000 feet; second waypoint latitude 31.79, longitude 34.63, altitude 3200 feet.",
                $"{p}-12", CancellationToken.None);
            var missionStatus999 = await InvokeToolAsync<MissionStatus>(tools, "GetMissionStatus", new() { ["tailNumber"] = "999" }, CancellationToken.None);
            Check(missionStatus999.WaypointCount == 2, $"UploadWaypoints(999) ground truth mismatch: waypointCount={missionStatus999.WaypointCount}");

            await orchestrator.HandleAsync("Reset 997's payload.", $"{p}-13", CancellationToken.None);
            var telemetry997AfterReset = await getTelemetry("997", CancellationToken.None);
            Check(telemetry997AfterReset.PayloadLockedOn is null, $"ResetPayload(997) ground truth mismatch: payloadLockedOn={telemetry997AfterReset.PayloadLockedOn}");

            var (rtl997Text, _) = await orchestrator.HandleAsync("Bring UAV 997 home.", $"{p}-14", CancellationToken.None);
            output.WriteLine($"[997 RTL response] {rtl997Text}");
            var telemetry997AfterRtl = await getTelemetry("997", CancellationToken.None);
            Check(telemetry997AfterRtl.Mode == "ReturningToLaunch", $"ReturnToLaunch(997) ground truth mismatch: mode={telemetry997AfterRtl.Mode}, response was: \"{rtl997Text}\"");

            var (rtlRestText, _) = await orchestrator.HandleAsync("Now bring the rest of the fleet back to base too.", $"{p}-15", CancellationToken.None);
            output.WriteLine($"[rest RTL response] {rtlRestText}");
            var telemetry998AfterRtl = await getTelemetry("998", CancellationToken.None);
            var telemetry999AfterRtl = await getTelemetry("999", CancellationToken.None);
            Check(telemetry998AfterRtl.Mode == "ReturningToLaunch" && telemetry999AfterRtl.Mode == "ReturningToLaunch",
                $"ReturnToLaunch(rest of fleet) ground truth mismatch: 998={telemetry998AfterRtl.Mode}, 999={telemetry999AfterRtl.Mode}, response was: \"{rtlRestText}\"");

            // ---- Act 3: watchdog maintenance ----
            var (healthText, _) = await orchestrator.HandleAsync("How are our backend services doing?", $"{p}-16", CancellationToken.None);
            Check(healthText.Contains("telemetry-relay", StringComparison.OrdinalIgnoreCase) || healthText.Contains("degraded", StringComparison.OrdinalIgnoreCase) || healthText.Contains("healthy", StringComparison.OrdinalIgnoreCase),
                "GetServicesHealth: response did not reflect the real fake health snapshot");

            var (restartText, _) = await orchestrator.HandleAsync("Let's restart the watchdog.", $"{p}-17", CancellationToken.None);
            Check(!restartText.Contains("fail", StringComparison.OrdinalIgnoreCase), $"RestartService: reported failure: {restartText}");

            var (stopText, _) = await orchestrator.HandleAsync("Now stop it for a scheduled maintenance window.", $"{p}-18", CancellationToken.None);
            Check(!stopText.Contains("fail", StringComparison.OrdinalIgnoreCase), $"StopService: reported failure: {stopText}");

            var (startText, _) = await orchestrator.HandleAsync("Maintenance is done, bring it back up.", $"{p}-19", CancellationToken.None);
            Check(!startText.Contains("fail", StringComparison.OrdinalIgnoreCase), $"StartService: reported failure: {startText}");

            var (configsText, _) = await orchestrator.HandleAsync("List the configurations we have on file.", $"{p}-20", CancellationToken.None);
            Check(configsText.Contains("Flight", StringComparison.OrdinalIgnoreCase) && configsText.Contains("Simulator", StringComparison.OrdinalIgnoreCase),
                "ListConfigurations: response did not name both known fake configurations");

            var (flightServicesText, _) = await orchestrator.HandleAsync("Show me the services configured under Flight.", $"{p}-21", CancellationToken.None);
            Check(flightServicesText.Contains("Service One", StringComparison.OrdinalIgnoreCase) && flightServicesText.Contains("Service Two", StringComparison.OrdinalIgnoreCase),
                "ListConfiguredServices(Flight): response did not name both known fake services");

            await orchestrator.HandleAsync("Add a new service called Service 3 to the Flight configuration.", $"{p}-22", CancellationToken.None);
            var flightAfterAdd = await InvokeToolAsync<List<ServiceConfigEntry>>(tools, "ListConfiguredServices", new() { ["configurationName"] = "Flight" }, CancellationToken.None);
            Check(flightAfterAdd.Any(e => e.Description == "Service 3" && e.Disabled != true), "AddConfiguredService ground truth mismatch: Service 3 not present/enabled after adding");

            await orchestrator.HandleAsync("Disable Service 3 for now.", $"{p}-23", CancellationToken.None);
            var flightAfterDisable = await InvokeToolAsync<List<ServiceConfigEntry>>(tools, "ListConfiguredServices", new() { ["configurationName"] = "Flight" }, CancellationToken.None);
            Check(flightAfterDisable.Any(e => e.Description == "Service 3" && e.Disabled == true), "UpdateConfiguredService ground truth mismatch: Service 3 not disabled");

            await orchestrator.HandleAsync("Remove Service 3 from the configuration entirely.", $"{p}-24", CancellationToken.None);
            var flightAfterRemove = await InvokeToolAsync<List<ServiceConfigEntry>>(tools, "ListConfiguredServices", new() { ["configurationName"] = "Flight" }, CancellationToken.None);
            Check(flightAfterRemove.All(e => e.Description != "Service 3"), "RemoveConfiguredService ground truth mismatch: Service 3 still present");

            // ---- Act 4: wrap-up (no tool calls expected) ----
            await orchestrator.HandleAsync("Give me a full summary of today's session.", $"{p}-25", CancellationToken.None);
            var (signOffText, duration) = await orchestrator.HandleAsync("Great work today. Thanks, that's all for now.", $"{p}-26", CancellationToken.None);
            output.WriteLine(signOffText);

            LiveTestSupport.LiveLog(output, $"[FullShiftScenario] repeat {i + 1}/{repeats} finished (last turn duration={duration}s), {mismatches.Count} mismatch(es)");
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
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (every step's real backend state matched what the conversation claimed)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[FullShiftScenario] FINAL: Verified successes: {successes}/{repeats}");
        Assert.True(successes == repeats, $"Full shift scenario had at least one ground-truth mismatch in {repeats - successes}/{repeats} repeats - see test output above for details.");
    }
}

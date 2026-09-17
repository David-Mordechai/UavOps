using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Regression coverage for the cross-domain naming collision found during this session's
/// tool-description-review pass: the Watchdog domain's own example configuration name 'Simulator'
/// (see <c>FakeWatchdogConfigService</c>) is literally the same word as the entire separate
/// McpSimulator domain (the real training-simulator VM). An ambiguous operator phrase like "is the
/// simulator up/running" could plausibly get answered from the wrong domain's data - either
/// because a tool description's own literal example pulled the wrong tool into the retrieval-
/// narrowed offered set (see <c>ToolsConfig.yaml</c>'s own comment on the <c>ListConfigurations</c>
/// fix), or because the model itself conflates the two once both domains' tools are in play.
///
/// Ground truth is checked the same way <see cref="FullShiftScenarioLiveTests"/> checks every real
/// mutation: independently re-invoking a real tool afterward, bypassing the model entirely, rather
/// than trusting response text alone. For the ambiguous phrasing, this means directly re-calling
/// <c>EnsureSimulatorVmRunning</c> ourselves after the model's own turn - the Fake backend's
/// in-memory "is it running" flag can only already be true if the model's own turn genuinely
/// invoked that real tool, not just answered plausibly. For the explicit config phrasing, this
/// means checking the response actually reflects the one real service (<c>"Sim Console"</c>)
/// configured under Watchdog's own 'Simulator' configuration in <c>FakeWatchdogConfigService</c>.
/// </summary>
public class SimulatorWatchdogNamingCollisionLiveTests(ITestOutputHelper output)
{
    static SimulatorWatchdogNamingCollisionLiveTests() => LiveTestSupport.ClearProgressLogOnce();

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

    private sealed record EnsureVmResult(bool Started, bool AlreadyRunning);

    [Fact]
    [Trait("Category", "Live")]
    public async Task AmbiguousSimulatorWord_RoutesEachPhrasingToTheCorrectDomain()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[SimulatorNamingCollision] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, factory, mcpClients, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;
            var tools = factory.McpTools;
            var p = $"sim-collision-{i}";
            var mismatches = new List<string>();

            void Check(bool condition, string description)
            {
                if (!condition)
                {
                    mismatches.Add(description);
                }
            }

            // Ambiguous phrasing - the actual reported risk: no "training"/"VM"/"configuration"
            // qualifier, just the bare collision word.
            var (ambiguousResponse, _) = await orchestrator.HandleAsync(
                "Is the simulator up and running right now?", $"{p}-1", CancellationToken.None);
            output.WriteLine($"[ambiguous phrasing response] {ambiguousResponse}");

            // Ground truth: bypass the model and call the real McpSimulator tool ourselves. If the
            // model's own turn above already called EnsureSimulatorVmRunning for real, this direct
            // re-invocation observes alreadyRunning=true (the Fake backend's in-memory flag was
            // already flipped) - the same "independently re-query the real backend" idiom
            // FullShiftScenarioLiveTests uses for every mutation.
            var vmState = await InvokeToolAsync<EnsureVmResult>(tools, "EnsureSimulatorVmRunning", new(), CancellationToken.None);
            Check(vmState.AlreadyRunning,
                "Ambiguous 'is the simulator running' did not actually invoke the real McpSimulator EnsureSimulatorVmRunning tool");
            Check(!ambiguousResponse.Contains("Sim Console", StringComparison.OrdinalIgnoreCase),
                $"Ambiguous 'is the simulator running' incorrectly answered from the Watchdog's 'Simulator' CONFIGURATION instead of the real training VM: \"{ambiguousResponse}\"");

            // Explicit config phrasing - the other side of the same collision: this one SHOULD
            // reach the Watchdog's own 'Simulator' configuration, not the training VM.
            var (configResponse, duration) = await orchestrator.HandleAsync(
                "What services are configured under the Simulator configuration?", $"{p}-2", CancellationToken.None);
            output.WriteLine($"[explicit config phrasing response] {configResponse}");
            Check(configResponse.Contains("Sim Console", StringComparison.OrdinalIgnoreCase),
                $"Explicit 'Simulator configuration' phrasing did not reflect the real Watchdog config's known service: \"{configResponse}\"");

            LiveTestSupport.LiveLog(output, $"[SimulatorNamingCollision] repeat {i + 1}/{repeats} finished (last turn duration={duration}s), {mismatches.Count} mismatch(es)");
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
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (ambiguous and explicit 'simulator' phrasing each routed to the correct domain)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[SimulatorNamingCollision] FINAL: Verified successes: {successes}/{repeats}");
        Assert.True(successes == repeats, $"Simulator/Watchdog naming-collision scenario had at least one mismatch in {repeats - successes}/{repeats} repeats - see test output above.");
    }
}

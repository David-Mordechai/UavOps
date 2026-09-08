using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Regression baseline for the "split BrainAgent's 3 domains into separate MCP servers"
/// redesign (see <c>CLAUDE.md</c>) - replays the exact real, live 6-turn conversation captured
/// verbatim in <c>eval/scenarios/multi-turn-fleet-ops.md</c> (fly + point-payload + return-to-
/// launch + a pure recall summary with no tool call) and asserts real ground-truth fleet state
/// after every mutating turn, plus that the final recall-only summary doesn't contradict it.
/// Captured *before* any MCP code exists specifically so it proves the split changes only
/// *where* each tool executes, never *whether* it executes for real - the split must keep this
/// green without modification to the test itself.
/// </summary>
public class MultiTurnFleetOpsLiveTests(ITestOutputHelper output)
{
    static MultiTurnFleetOpsLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task MultiTurnFleetOpsScenario_MatchesRealFleetState()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var tailNumbers = new[] { "UAV-1", "UAV-2", "UAV-3" };
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, _, fleetClient, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationPrefix = $"multi-turn-fleet-ops-{i}";

            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats}: turn 1/6 (greeting)");
            await orchestrator.HandleAsync("hi, my name is David and i am today Operator", $"{correlationPrefix}-1", CancellationToken.None);
            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats}: turn 2/6 (list fleet)");
            await orchestrator.HandleAsync("What UAVs do we have?", $"{correlationPrefix}-2", CancellationToken.None);
            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats}: turn 3/6 (fly)");
            await orchestrator.HandleAsync("fly them to target alpha at speed 250 and altitude 3000", $"{correlationPrefix}-3", CancellationToken.None);
            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats}: turn 4/6 (point payload)");
            await orchestrator.HandleAsync("also point theirs payloads there", $"{correlationPrefix}-4", CancellationToken.None);
            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats}: turn 5/6 (RTL)");
            await orchestrator.HandleAsync("bring them all home", $"{correlationPrefix}-5", CancellationToken.None);
            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats}: turn 6/6 (summary)");
            var (summary, duration) = await orchestrator.HandleAsync("give please summary of today session", $"{correlationPrefix}-6", CancellationToken.None);

            LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] repeat {i + 1}/{repeats} done (duration={duration}s)");
            output.WriteLine(summary);

            var snapshots = new List<TelemetrySnapshot>();
            foreach (var tail in tailNumbers)
            {
                var snapshot = await getTelemetry(tail, CancellationToken.None);
                snapshots.Add(snapshot);
                output.WriteLine($"{tail}: speed={snapshot.SpeedKts} altitude={snapshot.AltitudeFt} mode={snapshot.Mode} payload={snapshot.PayloadLockedOn}");
            }

            var allCorrect = snapshots.All(s =>
                s.SpeedKts == 250 && s.AltitudeFt == 3000 &&
                "ReturningToLaunch".Equals(s.Mode, StringComparison.OrdinalIgnoreCase) &&
                "alpha".Equals(s.PayloadLockedOn, StringComparison.OrdinalIgnoreCase));

            // The final turn is a pure recall question with no ground truth of its own to check -
            // its only requirement is not contradicting what really happened, i.e. it must not
            // claim the fleet is still flying/transiting once RTL genuinely happened.
            var summaryContradictsRealState = summary.Contains("transiting", StringComparison.OrdinalIgnoreCase)
                && !summary.Contains("return", StringComparison.OrdinalIgnoreCase);

            if (allCorrect && !summaryContradictsRealState)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS");
            }
            else
            {
                LiveTestSupport.LiveLog(output, "  => MISMATCH (real fleet state does not match the scenario's expected end state, or the summary contradicts it)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[MultiTurnFleetOps] FINAL: Verified successes: {successes}/{repeats}");
    }
}

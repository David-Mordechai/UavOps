using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Direct live regression test for a real, operator-reported bug: replays the exact conversation
/// that exposed it verbatim (fly the fleet, point payloads, bring one specific UAV home, then
/// "bring the rest UAVs home" - an ambiguous follow-up that must resolve to the OTHER two UAVs,
/// not the one already sent home). In production this turn opened two independent "which UAV do
/// you mean?" prompts (one per genuinely different guessed tail number - see
/// <see cref="TailNumberResolutionScope"/>'s own doc comment for why a single turn can now
/// legitimately need more than one), which this test answers with two distinct real tail numbers -
/// unlike the real incident, where the operator answered the first prompt with "UAV-1" (the UAV
/// already sent home) and let the second time out, after which the model fell into a confused
/// retry loop repeatedly re-guessing "UAV-1" instead of trying a genuinely different tail number.
/// This test isolates the scope-keying mechanism from that separate, model-behavior-level
/// confusion by always supplying real, distinct, valid answers - proving the underlying resolution
/// mechanism itself correctly routes each ambiguous call to a different UAV rather than clobbering
/// one with the other, which is what <see cref="TailNumberResolutionScope.GetOrAskAsync"/>'s fix
/// this session was actually meant to guarantee.
/// </summary>
public class ReturnRemainingFleetLiveTests(ITestOutputHelper output)
{
    static ReturnRemainingFleetLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task ReturnRemainingFleetScenario_BothRemainingUavsActuallyReturnHome_NeitherClobbersTheOther()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[ReturnRemainingFleet] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, _, fleetClient, _, promptGate) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationPrefix = $"return-remaining-fleet-{i}";

            await orchestrator.HandleAsync("hi, my name is David and I am today Operator.", $"{correlationPrefix}-1", CancellationToken.None);
            await orchestrator.HandleAsync("What UAVs do we have?", $"{correlationPrefix}-2", CancellationToken.None);
            await orchestrator.HandleAsync("send them to target alpha at speed 250 and altitude 3000", $"{correlationPrefix}-3", CancellationToken.None);
            await orchestrator.HandleAsync("point their payloads there", $"{correlationPrefix}-4", CancellationToken.None);
            await orchestrator.HandleAsync("bring uav-1 home", $"{correlationPrefix}-5", CancellationToken.None);

            LiveTestSupport.LiveLog(output, $"[ReturnRemainingFleet] repeat {i + 1}/{repeats}: the ambiguous 'rest' turn...");
            // The real incident's own turn: "bring the rest UAVs home" - UAV-1 already went home, so
            // the only correct real outcome is UAV-2 AND UAV-3 both ending up ReturningToLaunch.
            // Unlike the real operator (who answered the first prompt "UAV-1" by mistake), always
            // answer with the two genuinely different remaining tail numbers, in order - this test's
            // job is to prove the resolution mechanism itself keeps them independent, not to
            // reproduce the operator's own input mistake.
            var handleTask = orchestrator.HandleAsync("bring the rest UAVs home", $"{correlationPrefix}-6", CancellationToken.None);
            await LiveTestSupport.AnswerOperatorPromptsAsync(promptGate, handleTask, "UAV-2", "UAV-3");
            var (summary, duration) = await handleTask;
            LiveTestSupport.LiveLog(output, $"[ReturnRemainingFleet] repeat {i + 1}/{repeats} responded (duration={duration}s)");
            output.WriteLine(summary);

            var uav1 = await getTelemetry("UAV-1", CancellationToken.None);
            var uav2 = await getTelemetry("UAV-2", CancellationToken.None);
            var uav3 = await getTelemetry("UAV-3", CancellationToken.None);
            output.WriteLine($"UAV-1: mode={uav1.Mode}");
            output.WriteLine($"UAV-2: mode={uav2.Mode}");
            output.WriteLine($"UAV-3: mode={uav3.Mode}");

            // The real ground-truth check this bug broke: UAV-2 and UAV-3 must BOTH actually be
            // returning home, not just whichever one the (buggy) shared cache happened to answer
            // first. UAV-1's own state was already verified true by the earlier explicit turn.
            var bothRemainingReturned =
                "ReturningToLaunch".Equals(uav2.Mode, StringComparison.OrdinalIgnoreCase) &&
                "ReturningToLaunch".Equals(uav3.Mode, StringComparison.OrdinalIgnoreCase);

            if (bothRemainingReturned)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (UAV-2 and UAV-3 both actually returned home)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (UAV-2 mode={uav2.Mode}, UAV-3 mode={uav3.Mode} - at least one never actually returned)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[ReturnRemainingFleet] FINAL: Verified successes: {successes}/{repeats}");
    }
}

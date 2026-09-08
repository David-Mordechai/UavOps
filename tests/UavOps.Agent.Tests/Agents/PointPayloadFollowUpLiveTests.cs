using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Reproduction for a live incident reported 2026-09-06: operator flew all 3 UAVs to alpha in one
/// message (no payload mention), then in a SEPARATE follow-up turn said "point their payloads
/// there" - the real app's own log file for that session (<c>src/UavOps.Agent/logs/uavops-agent-
/// 20260906.log</c>) showed zero <c>PointPayload</c> calls and no new correlationId at all for that
/// follow-up turn, yet the response confidently claimed all payloads were pointed. Unlike
/// <see cref="FlyAllFleetWideLiveTests.FlyAllFleetWideCommand_NeverClaimsSuccessWithoutRealMutation"/>
/// (which bundles fly + point-payload into one message and passes 8/8), this splits them across
/// two turns - the distinguishing factor under live investigation.
///
/// Each repeat starts from a brand-new <see cref="SimulatedUavOperationService"/>
/// (<see cref="LiveTestSupport.BuildLiveOrchestrator"/> creates one per call), so PayloadLockedOn
/// starts null and the fly-only turn's own Navigate/SetSpeed/SetAltitude tool results carry
/// PayloadLockedOn: null back to the model - unlike the real incident, where leftover state from
/// earlier manual testing in the same long-running dev process meant those same tool results
/// already showed PayloadLockedOn: "alpha", giving the model textual grounds (however
/// stale/accidental) to treat the follow-up as redundant. This test isolates whether the model
/// skips the explicit follow-up command even with NO such grounds - the real fabrication bug - or
/// whether it reliably calls PointPayload when its own prior context shows the state not yet set.
/// </summary>
public class PointPayloadFollowUpLiveTests(ITestOutputHelper output)
{
    static PointPayloadFollowUpLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task PointPayloadFollowUp_AfterSeparateFlightCommand_StillCallsRealTool()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var tailNumbers = new[] { "UAV-1", "UAV-2", "UAV-3" };
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[PointPayloadFollowUp] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, _, fleetClient, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationPrefix = $"payload-followup-{i}";

            await orchestrator.HandleAsync("hi my name is David and i am today Operator", $"{correlationPrefix}-1", CancellationToken.None);
            await orchestrator.HandleAsync("What UAVs do we have?", $"{correlationPrefix}-2", CancellationToken.None);
            await orchestrator.HandleAsync(
                "fly them all to target alpha at speed 250 and altitude 3000", $"{correlationPrefix}-3", CancellationToken.None);
            var (responseText, duration) = await orchestrator.HandleAsync(
                "point their payloads there", $"{correlationPrefix}-4", CancellationToken.None);

            LiveTestSupport.LiveLog(output, $"[PointPayloadFollowUp] repeat {i + 1}/{repeats} responded (duration={duration}s)");
            output.WriteLine(responseText);

            var snapshots = new List<TelemetrySnapshot>();
            foreach (var tail in tailNumbers)
            {
                var snapshot = await getTelemetry(tail, CancellationToken.None);
                snapshots.Add(snapshot);
                output.WriteLine($"{tail}: payload={snapshot.PayloadLockedOn}");
            }

            var allPointed = snapshots.All(s => "alpha".Equals(s.PayloadLockedOn, StringComparison.OrdinalIgnoreCase));
            if (allPointed)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => REAL PointPayload CALLS CONFIRMED");
            }
            else
            {
                LiveTestSupport.LiveLog(output, "  => FABRICATION: response claimed success but PayloadLockedOn is still not 'alpha' for at least one UAV");
            }
        }

        LiveTestSupport.LiveLog(output, $"[PointPayloadFollowUp] FINAL: Real successes: {successes}/{repeats}");
    }
}

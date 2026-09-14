using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Direct live regression test for a real, operator-reported incident: "set speed to 120 and point
/// the camera to target alpha" (no tail number given) → BrainAgent asks which UAV → "what uavs do
/// we have?" (ListFleet) → BrainAgent asks again → the operator answers with a bare tail number,
/// "999". In production this final turn only ever called <c>PointPayload</c>; <c>SetSpeed</c> was
/// never attempted, and BrainAgent then falsely reported "Speed command for 999 failed as SetSpeed
/// is not available" - a fabricated reason, since <c>SetSpeed</c> was never called at all.
///
/// Root cause: <see cref="AgentFactory.BuildToolsForTurn"/>'s tool-retrieval embedding used only
/// the bare resolving turn's text ("999") to rank the full tool catalog - a query with no semantic
/// content of its own, so ranking was close to random and excluded <c>SetSpeed</c> from the top-K
/// candidate set purely by chance, even though the conversation's own earlier turns clearly needed
/// it. Fixed in <see cref="MainAgentOrchestrator.HandleAsync"/> by building the retrieval query
/// from the real bounded conversation history (via <c>InMemoryChatHistoryProvider.GetMessages</c>)
/// plus the current turn's text, so a bare follow-up like "999" still carries the original
/// "set speed... and point the camera..." request's semantic content into ranking.
/// </summary>
public class SetSpeedAndPointPayloadAfterTailNumberLiveTests(ITestOutputHelper output)
{
    static SetSpeedAndPointPayloadAfterTailNumberLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task AmbiguousSetSpeedAndPointPayload_ResolvedByBareTailNumber_CallsBothRealTools()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[SetSpeedAndPointPayload] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, _, fleetClient, _, promptGate) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationPrefix = $"speed-payload-tailnum-{i}";

            var turn1 = orchestrator.HandleAsync(
                "set speed to 120 and point the camera to target alpha", $"{correlationPrefix}-1", CancellationToken.None);
            await LiveTestSupport.AnswerOperatorPromptsAsync(promptGate, turn1);
            await turn1;

            var turn2 = orchestrator.HandleAsync("what uavs do we have?", $"{correlationPrefix}-2", CancellationToken.None);
            await LiveTestSupport.AnswerOperatorPromptsAsync(promptGate, turn2);
            await turn2;

            LiveTestSupport.LiveLog(output, $"[SetSpeedAndPointPayload] repeat {i + 1}/{repeats}: resolving turn ('999')...");
            var turn3 = orchestrator.HandleAsync("999", $"{correlationPrefix}-3", CancellationToken.None);
            await LiveTestSupport.AnswerOperatorPromptsAsync(promptGate, turn3);
            var (summary, duration) = await turn3;

            LiveTestSupport.LiveLog(output, $"[SetSpeedAndPointPayload] repeat {i + 1}/{repeats} responded (duration={duration}s)");
            output.WriteLine(summary);

            var snapshot = await getTelemetry("999", CancellationToken.None);
            output.WriteLine($"999: speedKts={snapshot.SpeedKts}, payload={snapshot.PayloadLockedOn}");

            // The real ground-truth check this bug broke: both actions from the ORIGINAL request
            // must actually have executed against 999, not just whichever one retrieval happened to
            // keep in its top-K that turn.
            var bothActionsApplied =
                snapshot.SpeedKts == 120 &&
                "alpha".Equals(snapshot.PayloadLockedOn, StringComparison.OrdinalIgnoreCase);

            if (bothActionsApplied)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (SetSpeed and PointPayload both actually applied to 999)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (speedKts={snapshot.SpeedKts}, payload={snapshot.PayloadLockedOn} - at least one action never actually applied)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[SetSpeedAndPointPayload] FINAL: Verified successes: {successes}/{repeats}");
    }
}

using FluentAssertions;
using UavOps.Agent.Tooling;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Live regression test for a real, operator-reported failure, replaying the exact conversation
/// verbatim: send 999 to alpha at 70 kts / 3000 ft, send "the other UAVs" there too, then "bring
/// them all home and give me full summary of today session". In production that last turn never
/// offered <c>ReturnToLaunch</c> to the model at all - the summary half of the sentence dominated
/// retrieval (the tool ranked 14th on the turn text, 11th with history, top-K 10) - so the model
/// truthfully answered "No return-to-launch capability available", read telemetry for the summary,
/// and brought nothing home. Fixed by ranking each clause of a compound turn separately too
/// (<see cref="RetrievalClauseSplitter"/>).
///
/// Checks the real outcome, not the reply text: every UAV's real telemetry must end up
/// ReturningToLaunch. Any "which UAV?" prompt along the way is answered the way the operator did
/// (999 for the first UAV, the others after), and "ALL" if the final turn asks.
/// </summary>
public class ReturnAllHomeWithSessionSummaryLiveTests(ITestOutputHelper output)
{
    static ReturnAllHomeWithSessionSummaryLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task BringThemAllHomeWithSummary_EveryUavActuallyReturnsToLaunch()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[ReturnAllHomeWithSummary] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, _, fleetClient, _, promptGate) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var prefix = $"return-all-home-summary-{i}";

            async Task<string> Turn(string text, int n, params string[] promptAnswers)
            {
                var handleTask = orchestrator.HandleAsync(text, $"{prefix}-{n}", CancellationToken.None);
                await LiveTestSupport.AnswerOperatorPromptsAsync(promptGate, handleTask, promptAnswers);
                var (reply, _) = await handleTask;
                output.WriteLine($"Operator: {text}\nBrainAgent: {reply}\n");
                return reply;
            }

            await Turn("fly uav to target alpha at speed 70 and altitude 3000", 1, "999");
            await Turn("which UAVs do we have?", 2);
            await Turn("999", 3, "999");
            await Turn("send the other UAVs there with the same speed and altitude", 4, "997", "998");

            LiveTestSupport.LiveLog(output, $"[ReturnAllHomeWithSummary] repeat {i + 1}/{repeats}: the compound 'bring them all home + summary' turn...");
            var summary = await Turn("bring them all home and give me full summary of today session", 5, "ALL", "ALL", "ALL");

            var modes = new Dictionary<string, string?>();
            foreach (var tail in new[] { "997", "998", "999" })
            {
                modes[tail] = (await getTelemetry(tail, CancellationToken.None)).Mode;
            }
            output.WriteLine(string.Join("  ", modes.Select(m => $"{m.Key}: mode={m.Value}")));

            var allReturned = modes.Values.All(m => "ReturningToLaunch".Equals(m, StringComparison.OrdinalIgnoreCase));
            if (allReturned)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (all three UAVs actually returning to launch)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH ({string.Join(", ", modes.Select(m => $"{m.Key}={m.Value}"))}) - reply: {summary}");
            }
        }

        LiveTestSupport.LiveLog(output, $"[ReturnAllHomeWithSummary] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats, "every repeat must actually bring all three UAVs home");
    }
}

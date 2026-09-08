using UavOps.Agent.Agents;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Reproduction for a second live incident reported 2026-09-06: after asking "What UAVs do we
/// have?" once early in the conversation (a real <c>ListFleet</c> call, logged), then flying the
/// fleet (which changes every UAV's mode to Transiting and its position), asking "What UAVs do we
/// have?" a SECOND time returned the exact same stale "Orbiting" mode and default coordinates from
/// the FIRST call, verbatim - <c>src/UavOps.Agent/logs/uavops-agent-20260906.log</c> shows no
/// <c>ListFleet</c> call at all after the one from ~20 minutes and several fleet-state mutations
/// earlier. This is a read-query analogue of the payload-follow-up bug: once real tool-call
/// history exists, the model answers from that memory instead of re-invoking the tool - for a
/// status query, that means confidently reporting fleet state that is objectively wrong.
///
/// No ground-truth side effect exists to check for a read-only query (unlike the mutation tests),
/// so this checks the response text itself for the exact failure signature actually observed
/// live: claiming "Orbiting" (the stale, pre-flight mode) instead of reflecting the real
/// post-flight "Transiting" mode.
/// </summary>
public class RepeatedFleetQueryLiveTests(ITestOutputHelper output)
{
    static RepeatedFleetQueryLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task RepeatedFleetQuery_AfterStateChange_ReflectsCurrentStateNotStaleHistory()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var freshAnswers = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[RepeatedFleetQuery] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, fleetClient, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationPrefix = $"stale-fleet-query-{i}";

            await orchestrator.HandleAsync("hi my name is David and i am today Operator", $"{correlationPrefix}-1", CancellationToken.None);
            await orchestrator.HandleAsync("What UAVs do we have?", $"{correlationPrefix}-2", CancellationToken.None);
            await orchestrator.HandleAsync(
                "fly them all to target alpha at speed 250 and altitude 3000", $"{correlationPrefix}-3", CancellationToken.None);
            var (responseText, duration) = await orchestrator.HandleAsync(
                "What UAVs do we have?", $"{correlationPrefix}-4", CancellationToken.None);

            LiveTestSupport.LiveLog(output, $"[RepeatedFleetQuery] repeat {i + 1}/{repeats} responded (duration={duration}s)");
            output.WriteLine(responseText);

            var mentionsStaleOrbiting = responseText.Contains("orbiting", StringComparison.OrdinalIgnoreCase);
            var mentionsTransiting = responseText.Contains("transit", StringComparison.OrdinalIgnoreCase);

            if (mentionsTransiting && !mentionsStaleOrbiting)
            {
                freshAnswers++;
                LiveTestSupport.LiveLog(output, "  => FRESH, CORRECT ANSWER");
            }
            else
            {
                LiveTestSupport.LiveLog(output, "  => STALE/WRONG ANSWER (reports pre-flight state after the fleet already moved)");
            }
        }

        LiveTestSupport.LiveLog(output, $"[RepeatedFleetQuery] FINAL: Fresh answers: {freshAnswers}/{repeats}");
    }
}

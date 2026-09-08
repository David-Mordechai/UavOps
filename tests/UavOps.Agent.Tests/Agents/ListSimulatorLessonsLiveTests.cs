using UavOps.Agent.Agents;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// New scenario, authored for the Simulator-domain MCP split (<c>UavOps.Agent.McpSimulator</c>)
/// - a plain "what lessons are available" query, exercising a real MCP round trip to that
/// server's <c>ListSimulatorLessons</c> tool with no confirmation/prompt involved. Asserts
/// against the real fake-backend lesson list (<c>FakeLessonExecutor.FakeLessons</c>), not just
/// that the model said something plausible-sounding.
/// </summary>
public class ListSimulatorLessonsLiveTests(ITestOutputHelper output)
{
    static ListSimulatorLessonsLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task ListSimulatorLessons_MentionsTheKnownFakeLessons()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var knownLessons = new[] { "intro-flight-basics", "advanced-navigation", "emergency-procedures" };
        var correct = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[ListSimulatorLessons] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, fleetClient, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;

            var (responseText, duration) = await orchestrator.HandleAsync(
                "what training lessons are available in the simulator?", $"sim-lessons-{i}", CancellationToken.None);

            LiveTestSupport.LiveLog(output, $"[ListSimulatorLessons] repeat {i + 1}/{repeats} responded (duration={duration}s)");
            output.WriteLine(responseText);

            var mentionsAll = knownLessons.All(lesson => responseText.Contains(lesson, StringComparison.OrdinalIgnoreCase));
            if (mentionsAll)
            {
                correct++;
                LiveTestSupport.LiveLog(output, "  => ALL KNOWN LESSONS MENTIONED");
            }
            else
            {
                LiveTestSupport.LiveLog(output, "  => MISSING AT LEAST ONE KNOWN LESSON NAME");
            }
        }

        LiveTestSupport.LiveLog(output, $"[ListSimulatorLessons] FINAL: Correct: {correct}/{repeats}");
    }
}

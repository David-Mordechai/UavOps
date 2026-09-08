using UavOps.Agent.Agents;
using UavOps.Agent.Tooling;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// New scenario, authored for the Simulator-domain MCP split - the full training-lesson launch
/// pipeline end to end: naming a lesson directly (exercising <c>AskOperatorWhichLesson</c>'s
/// deterministic auto-resolve - see <c>UavOps.Agent.McpSimulator.LessonChoiceResolver</c>'s own
/// doc comment - since the operator's text names exactly one of the discovered lessons), then
/// approving the confirmation <c>RunSimulatorLesson</c> requires (its own MCP tool annotation -
/// see <c>AgentFactory.RequiresConfirmation</c>) via
/// <see cref="LiveTestSupport.ApproveAnyPendingConfirmationAsync"/>. <c>RunSimulatorLesson</c>'s
/// own tool description instructs the model to report only that the lesson was queued/started,
/// never a final pass/fail outcome (the real run happens in the background, in a separate
/// process, and is reported later via a proactive push this in-process harness has no live host
/// to receive - see <c>UavOps.Agent.McpSimulator.HubLessonOutcomeNotifier</c> - so that
/// notification path is out of scope for this test and is checked instead by
/// <c>UavOps.Agent.McpSimulator</c>'s own unit tests). No ground-truth side effect is
/// independently observable here (same limitation
/// <see cref="RepeatedFleetQueryLiveTests.RepeatedFleetQuery_AfterStateChange_ReflectsCurrentStateNotStaleHistory"/>
/// already documents for a read-only query) - <see cref="ToolInvocationLogger.GetAgentInvocationCount"/>
/// looked like a candidate but reads 0 unconditionally here, since
/// <see cref="MainAgentOrchestrator.HandleAsync"/> clears invocation tracking for the
/// correlationId before returning, i.e. before this test ever gets to check it - confirmed live
/// (0/8 by that measure despite a real, correctly-worded response every single time, including
/// once the exact lesson filename with its .ps1 extension, which the operator's own text never
/// contained). So this checks the response text only, same as that sibling test.
/// </summary>
public class RunSimulatorLessonLiveTests(ITestOutputHelper output)
{
    static RunSimulatorLessonLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task RunSimulatorLessonScenario_ConfirmsAndReportsQueuedNotFinalOutcome()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[RunSimulatorLesson] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, _, fleetClient, confirmationGate, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationId = $"sim-run-lesson-{i}";

            var handleTask = orchestrator.HandleAsync("run the intro-flight-basics lesson", correlationId, CancellationToken.None);
            await LiveTestSupport.ApproveAnyPendingConfirmationAsync(confirmationGate, handleTask);
            var (responseText, duration) = await handleTask;

            LiveTestSupport.LiveLog(output, $"[RunSimulatorLesson] repeat {i + 1}/{repeats} responded (duration={duration}s)");
            output.WriteLine(responseText);

            var claimsQueuedOrStarted = responseText.Contains("queue", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("start", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("running", StringComparison.OrdinalIgnoreCase);
            var claimsFinalOutcome = responseText.Contains("completed", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("finished", StringComparison.OrdinalIgnoreCase);

            output.WriteLine($"  queued/started wording: {claimsQueuedOrStarted}, premature final-outcome wording: {claimsFinalOutcome}");

            if (claimsQueuedOrStarted && !claimsFinalOutcome)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (honest 'queued' framing, no premature outcome claim)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, "  => MISMATCH");
            }
        }

        LiveTestSupport.LiveLog(output, $"[RunSimulatorLesson] FINAL: Verified successes: {successes}/{repeats}");
    }
}

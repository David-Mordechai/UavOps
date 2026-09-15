using UavOps.Agent.Voice;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Direct live regression test for a real, operator-reported voice session bug: "Who was today's
/// separator?" (a mishearing of "operator") was never corrected by the grammar-fix pass
/// (<see cref="VoiceGatewayService.FixGrammarAsync"/>), because it had no way to know "operator"
/// was the live topic of conversation - it saw one bare line with no context at all. Fixed by
/// having <see cref="VoiceGatewayService"/> read BrainAgent's own real, persistent conversation
/// history (<see cref="Agents.AgentFactory.GetRecentBrainAgentMessagesAsync"/>) for exactly this
/// disambiguation, used read-only, never as something to act on or reply to. This test establishes
/// "operator" as the live topic the same way the real session did, then calls
/// <see cref="VoiceGatewayService.FixGrammarAsync"/> directly (bypassing the real STT wire hop,
/// which is orthogonal to what's being verified here).
///
/// Its own class (not a method alongside the other voice grammar-fix scenarios) specifically so
/// xUnit runs it concurrently with them - see <see cref="LiveTestSupport"/>'s own doc comment for
/// why methods within one class never parallelize regardless of what else is true.
/// </summary>
public class VoiceGrammarFixHomophoneLiveTests(ITestOutputHelper output)
{
    static VoiceGrammarFixHomophoneLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task GrammarFix_GivenOperatorHomophoneMishearing_UsesConversationContextToCorrectIt()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[VoiceGrammarFix-Homophone] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, _, factory, fleetClient, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;
            var correlationPrefix = $"voice-grammar-fix-homophone-{i}";

            // Establishes "operator" as the live topic, the same way the real session did, before
            // the mishearing ever comes up.
            await orchestrator.HandleAsync("Hi, my name is David, and I am the operator today.", $"{correlationPrefix}-1", CancellationToken.None);

            var voice = LiveTestSupport.BuildVoiceGatewayService(factory);
            var corrected = await voice.FixGrammarAsync("Who was today's separator?", CancellationToken.None);
            output.WriteLine($"[repeat {i + 1}] corrected: \"{corrected}\"");

            var correctedToOperator = corrected.Contains("operator", StringComparison.OrdinalIgnoreCase);
            var stillSaysSeparator = corrected.Contains("separator", StringComparison.OrdinalIgnoreCase);

            if (correctedToOperator && !stillSaysSeparator)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS ('separator' corrected to 'operator' using conversation context)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (did not correct 'separator' -> 'operator'): \"{corrected}\"");
            }
        }

        LiveTestSupport.LiveLog(output, $"[VoiceGrammarFix-Homophone] FINAL: Verified successes: {successes}/{repeats}");
        Assert.True(successes == repeats, $"Grammar-fix failed to correct the 'operator'/'separator' homophone using conversation context in {repeats - successes}/{repeats} repeats.");
    }
}

using UavOps.Agent.Voice;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Direct live regression test for a real, operator-reported voice session bug: after tail numbers
/// were renamed from "UAV-N" to bare digits (997/998/999 - see CLAUDE.md), "UAV 999" spoken
/// quickly was misheard as "your V999" - the leading "UAV" merged with the digits into a single
/// "V&lt;number&gt;" token, which the grammar-fix pass (<see cref="VoiceGatewayService.FixGrammarAsync"/>)
/// left untouched (the existing "UABs -&gt; UAVs" mishearing rule never matched, since there's no
/// bare "UAV"-shaped word left to fix, and "do not change tail numbers" made it leave "V999" alone
/// as if it looked like a tail number itself). Fixed by an explicit rule telling the model that a
/// single letter immediately followed by digits like this should be split back into "UAV" plus the
/// bare number, keeping the digits unchanged. This is a downstream text-normalization patch only -
/// it does not explain or fix whatever in the actual STT model caused the mishearing (BrainAgent's
/// own tail-number resolution already extracted the right UAV from "V999" regardless; this only
/// makes the displayed transcript itself read correctly).
///
/// Its own class (not a method alongside the other voice grammar-fix scenarios) specifically so
/// xUnit runs it concurrently with them - see <see cref="LiveTestSupport"/>'s own doc comment for
/// why methods within one class never parallelize regardless of what else is true.
/// </summary>
public class VoiceGrammarFixUavMergeLiveTests(ITestOutputHelper output)
{
    static VoiceGrammarFixUavMergeLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task GrammarFix_GivenUavMergedWithTailNumber_SplitsItBackIntoUavPlusBareNumber()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[VoiceGrammarFix-UavMerge] starting repeat {i + 1}/{repeats}...");
            var (_, _, _, factory, fleetClient, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = fleetClient;

            var voice = LiveTestSupport.BuildVoiceGatewayService(factory);
            var corrected = await voice.FixGrammarAsync("Bring your V999 home.", CancellationToken.None);
            output.WriteLine($"[repeat {i + 1}] corrected: \"{corrected}\"");

            var splitBackToUav = corrected.Contains("UAV 999", StringComparison.OrdinalIgnoreCase);
            var stillMerged = corrected.Contains("V999", StringComparison.OrdinalIgnoreCase);

            if (splitBackToUav && !stillMerged)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS ('V999' split back into 'UAV 999')");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (did not split 'V999' -> 'UAV 999'): \"{corrected}\"");
            }
        }

        LiveTestSupport.LiveLog(output, $"[VoiceGrammarFix-UavMerge] FINAL: Verified successes: {successes}/{repeats}");
        Assert.True(successes == repeats, $"Grammar-fix failed to split the merged 'V999' mishearing back into 'UAV 999' in {repeats - successes}/{repeats} repeats.");
    }
}

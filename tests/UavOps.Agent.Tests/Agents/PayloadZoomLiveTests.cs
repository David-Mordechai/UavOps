using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// The payload zoom through the real model: "zoom 997 to 10x" must call SetPayloadZoom (the only
/// way the zoom changes - the operator asked that the payload move only by tool, so a real UAV's
/// payload is commanded the same way), "zoom in more" must zoom further from there, and pointing the
/// payload afterwards must not touch the zoom. Neither may change the altitude. Checked against the
/// backend's real telemetry after each turn.
/// </summary>
public class PayloadZoomLiveTests(ITestOutputHelper output)
{
    static PayloadZoomLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task ZoomCommands_SetTheRealPayloadZoom_AndNothingElse()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[PayloadZoom] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, _, mcpClients, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;
            var correlationPrefix = $"payload-zoom-{i}";

            var (zoomReply, _) = await orchestrator.HandleAsync("zoom 997's camera to 10x", $"{correlationPrefix}-1", CancellationToken.None);
            output.WriteLine($"[zoom 10x] {zoomReply}");
            var afterZoom = await getTelemetry("997", CancellationToken.None);

            var (moreReply, _) = await orchestrator.HandleAsync("zoom in more", $"{correlationPrefix}-2", CancellationToken.None);
            output.WriteLine($"[zoom in more] {moreReply}");
            var afterMore = await getTelemetry("997", CancellationToken.None);

            var (pointReply, _) = await orchestrator.HandleAsync("point 997's payload at alpha", $"{correlationPrefix}-3", CancellationToken.None);
            output.WriteLine($"[point] {pointReply}");
            var afterPoint = await getTelemetry("997", CancellationToken.None);

            output.WriteLine($"997 zoom: {afterZoom.PayloadZoom} -> {afterMore.PayloadZoom} -> {afterPoint.PayloadZoom}; " +
                             $"altitude {afterPoint.AltitudeFt}; payload {afterPoint.PayloadLockedOn}");

            var ok = afterZoom.PayloadZoom == 10 &&
                     afterMore.PayloadZoom > 10 &&
                     afterPoint.PayloadZoom == afterMore.PayloadZoom &&
                     "alpha".Equals(afterPoint.PayloadLockedOn, StringComparison.OrdinalIgnoreCase) &&
                     afterPoint.AltitudeFt == 4000;
            if (ok)
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (zoom 10x, zoomed in further, pointed without re-zooming, altitude unchanged)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (zoom {afterZoom.PayloadZoom} -> {afterMore.PayloadZoom} -> {afterPoint.PayloadZoom}, " +
                                                $"payload {afterPoint.PayloadLockedOn}, altitude {afterPoint.AltitudeFt})");
            }
        }

        LiveTestSupport.LiveLog(output, $"[PayloadZoom] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats);
    }
}

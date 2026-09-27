using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpMoav;
using UavOps.Agent.Mission;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// After the fleet reports a detection, "send 998 to the white van" must fly 998 to the reported
/// position. The detection goes through McpMoav's real <see cref="MissionEventService"/>, with its
/// operator message and history note delivered into the live factory's history journal the way
/// ChatHub.PostOperatorMessage does in production (these tests have no hub). Its
/// DetectionPointRegistry is this process's, not the spawned McpMoav's, so only the "lat,lng" the
/// note tells the model to use can reach the van here - the by-name fallback is covered by
/// MoavMissionToolsTests. Success is 998's real telemetry at the van's coordinates, not anything
/// the response says.
/// </summary>
public class NavigateToDetectionLiveTests(ITestOutputHelper output)
{
    static NavigateToDetectionLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    private const double VanLat = 31.81380;
    private const double VanLng = 34.66521;

    [Fact]
    [Trait("Category", "Live")]
    public async Task SendUavToTheDetectedObject_FliesToItsReportedPosition()
    {
        var repeats = LiveTestSupport.RepeatCount;
        var successes = 0;

        for (var i = 0; i < repeats; i++)
        {
            LiveTestSupport.LiveLog(output, $"[NavigateToDetection] starting repeat {i + 1}/{repeats}...");
            var (orchestrator, _, getTelemetry, factory, mcpClients, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
            await using var _ = mcpClients;

            var detections = new MissionEventService(new JournalNotifier(factory.ProactiveJournal), new DetectionPointRegistry(),
                new MissionOptions(), NullLogger<MissionEventService>.Instance);

            await orchestrator.HandleAsync("hi, I'm the operator today", $"nav-detection-{i}-1", CancellationToken.None);
            await detections.HandleDetectionAsync(new DetectionReport(
                "997", "ZoneA-997-abc123", "ZoneA", "white van", "van", 0.9, VanLat, VanLng, DateTime.UtcNow, "t1"));

            var (summary, _) = await orchestrator.HandleAsync("send 998 to the white van", $"nav-detection-{i}-2", CancellationToken.None);
            output.WriteLine(summary);

            var uav = await getTelemetry("998", CancellationToken.None);
            output.WriteLine($"998: {uav.Lat:F5}, {uav.Lng:F5} mode={uav.Mode}");

            if (Math.Abs(uav.Lat - VanLat) < 1e-4 && Math.Abs(uav.Lng - VanLng) < 1e-4 && uav.Mode == "Transiting")
            {
                successes++;
                LiveTestSupport.LiveLog(output, "  => VERIFIED SUCCESS (998 sent to the van's reported position)");
            }
            else
            {
                LiveTestSupport.LiveLog(output, $"  => MISMATCH (998 at {uav.Lat:F5}, {uav.Lng:F5}, mode {uav.Mode})");
            }
        }

        LiveTestSupport.LiveLog(output, $"[NavigateToDetection] FINAL: Verified successes: {successes}/{repeats}");
        successes.Should().Be(repeats);
    }

    /// <summary>What ChatHub.PostOperatorMessage/AddHistoryNote do with a note, minus the chat push.</summary>
    private sealed class JournalNotifier(ProactiveHistoryJournal journal) : IOperatorNotifier
    {
        public Task PostAsync(string message, string? historyNote)
        {
            if (historyNote is not null)
                journal.Add(historyNote, message);
            return Task.CompletedTask;
        }

        public Task AddHistoryNoteAsync(string note, string message)
        {
            journal.Add(note, message);
            return Task.CompletedTask;
        }
    }
}

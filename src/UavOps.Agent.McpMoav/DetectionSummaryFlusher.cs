using Microsoft.Extensions.Hosting;

namespace UavOps.Agent.McpMoav;

/// <summary>Posts <see cref="MissionEventService"/>'s detection summaries when they fall due.</summary>
public sealed class DetectionSummaryFlusher(MissionEventService missionEvents) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await missionEvents.FlushDueSummariesAsync(DateTime.UtcNow);
    }
}

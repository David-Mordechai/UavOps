using UavOps.Agent.Options;

namespace UavOps.Agent.Agents.MaintenanceAgent;

/// <summary>
/// Polls the watchdog's HTTP health-check endpoint every <see cref="WatchdogOptions.PollIntervalSeconds"/>
/// and pushes each result into <see cref="IWatchdogHealthStore"/>, so
/// <c>WatchdogService.GetServicesHealth</c> is a plain in-memory read instead of a live HTTP call
/// on every tool invocation. Registered only under <see cref="WatchdogBackend.Real"/> — there's
/// nothing to poll under <see cref="WatchdogBackend.Fake"/>.
///
/// One bad poll (endpoint down, malformed JSON, timeout) is logged and skipped rather than
/// stopping the loop — same reasoning <c>SimulatorLessonJobProcessor</c>'s per-job try/catch
/// exists for: a single failure must not take down the whole background service.
/// </summary>
public sealed class WatchdogHealthPoller(
    IHttpClientFactory httpClientFactory,
    IWatchdogHealthStore store,
    WatchdogOptions options,
    ILogger<WatchdogHealthPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Watchdog health poll failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Watchdog");
        var json = await client.GetStringAsync(options.HealthCheckUrl, cancellationToken);
        var snapshot = WatchdogHealthParser.Parse(json, DateTimeOffset.UtcNow);
        store.Update(snapshot);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Consumes <see cref="ISimulatorLessonJobQueue"/> one job at a time — the queue itself provides
/// the serialization, no separate "busy" check needed. For each job: runs it via
/// <see cref="ILessonExecutor"/> (the "what happened" step — deterministic, never sees a small
/// model try to parse raw script output), then reports the outcome to <c>UavOps.Agent</c> via
/// <see cref="ILessonOutcomeNotifier"/> for the "how to say it" step (building the "simple terms"
/// sentence needs BrainAgent's own persona/model, a host-only concept - see
/// <see cref="ILessonOutcomeNotifier"/>'s own doc comment).
///
/// Runs under this service's own lifetime token (only cancels on this process's shutdown, not the
/// originating chat request's) — a browser disconnecting mid-lesson can no longer affect a run
/// already handed off to the queue.
/// </summary>
public sealed class SimulatorLessonJobProcessor(
    ISimulatorLessonJobQueue queue,
    ILessonExecutor executor,
    ILessonOutcomeNotifier notifier,
    ILogger<SimulatorLessonJobProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "background lesson job for {LessonName} failed unexpectedly", job.LessonName);
            }
        }
    }

    private async Task ProcessAsync(SimulatorLessonJob job, CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        var (outcome, detail) = await executor.ExecuteAsync(job.LessonName, stoppingToken);
        sw.Stop();

        logger.LogInformation("background lesson job for {LessonName} finished: {Outcome} ({DurationMs}ms)",
            job.LessonName, outcome, sw.ElapsedMilliseconds);

        await notifier.PushAsync(job.LessonName, outcome, detail, sw.Elapsed, stoppingToken);
    }
}

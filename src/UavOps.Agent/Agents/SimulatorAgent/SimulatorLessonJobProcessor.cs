using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;

namespace UavOps.Agent.Agents.SimulatorAgent;

/// <summary>
/// Consumes <see cref="ISimulatorLessonJobQueue"/> one job at a time — the queue itself provides
/// the serialization, no separate "busy" check needed. For each job: runs it via
/// <see cref="ILessonExecutor"/> (the "what happened" step — deterministic, never sees a small
/// model try to parse raw script output), then builds a tools-stripped instance of
/// <c>SimulatorInfrastructureAgent</c> (<see cref="AgentFactory.BuildPersonaOnlyAgent"/> — same
/// persona/voice as the real agent, but structurally unable to call any tool again) and runs it
/// once with a synthetic instruction summarizing the concise outcome, to produce the "simple
/// terms" sentence the operator actually wants (the "how to say it" step). The result is pushed as
/// an ordinary <c>ReceiveChatMessage</c> under a fresh correlationId — the chat UI already renders
/// any such message as a new bubble the first time it sees that correlationId, so this appears as
/// a new, unprompted message in the thread with no frontend changes needed.
///
/// Runs under this service's own lifetime token (only cancels on app shutdown), not the
/// originating chat request's — a browser disconnecting mid-lesson can no longer affect a run
/// already handed off to the queue.
/// </summary>
public sealed class SimulatorLessonJobProcessor(
    ISimulatorLessonJobQueue queue,
    ILessonExecutor executor,
    AgentFactory agentFactory,
    IHubContext<ChatHub> hub,
    ILogger<SimulatorLessonJobProcessor> logger) : BackgroundService
{
    private const string LessonAgentName = "SimulatorInfrastructureAgent";

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
                logger.LogError(ex, "correlationId={CorrelationId} background lesson job for {LessonName} failed unexpectedly",
                    job.CorrelationId, job.LessonName);
            }
        }
    }

    private async Task ProcessAsync(SimulatorLessonJob job, CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        var (outcome, detail) = await executor.ExecuteAsync(job.LessonName, stoppingToken);
        sw.Stop();

        logger.LogInformation("correlationId={CorrelationId} background lesson job for {LessonName} finished: {Outcome} ({DurationMs}ms)",
            job.CorrelationId, job.LessonName, outcome, sw.ElapsedMilliseconds);

        var instruction = BuildSummaryInstruction(job.LessonName, outcome, detail, sw.Elapsed);
        var summaryAgent = agentFactory.BuildPersonaOnlyAgent(LessonAgentName);
        var response = await summaryAgent.RunAsync(instruction, cancellationToken: stoppingToken);

        var proactiveCorrelationId = Guid.NewGuid().ToString("N")[..8];
        await hub.Clients.All.SendAsync("ReceiveChatMessage", LessonAgentName, response.Text, sw.Elapsed.TotalSeconds,
            proactiveCorrelationId, stoppingToken);
    }

    private static string BuildSummaryInstruction(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration)
    {
        var outcomeText = outcome switch
        {
            LessonOutcome.Succeeded => "Succeeded, no problems detected.",
            LessonOutcome.SucceededWithWarnings => $"Succeeded, but with a warning: {detail}",
            LessonOutcome.Failed => $"Failed: {detail}",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };

        return $"You just finished running lesson '{lessonName}' in the background (it took {duration.TotalSeconds:0.#}s). " +
               $"Outcome: {outcomeText} Tell the operator this outcome in one or two short, plain sentences. " +
               "Do not invent anything beyond what's given here, and do not mention tool or operation names.";
    }
}

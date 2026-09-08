using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Establishes <see cref="HubConnection"/>'s first connection to the host's <c>/chatHub</c> in the
/// background, with its own retry loop — never blocking this server's own startup on host
/// reachability. This domain's tools (VM control, lesson listing, even enqueuing a lesson run) are
/// otherwise fully independent of the host being up; only the eventual outcome-notification push
/// (<see cref="HubLessonOutcomeNotifier"/>, called later by <see cref="SimulatorLessonJobProcessor"/>)
/// actually needs this connection, and that call already tolerates failure (logged, not fatal — see
/// <see cref="SimulatorLessonJobProcessor.ExecuteAsync"/>). <see cref="HubConnectionBuilder.WithAutomaticReconnect"/>
/// only takes over after a connection has been established once, so the *first* attempt still needs
/// its own retry here. Without this, a synchronous <c>await hubConnection.StartAsync()</c> in
/// <c>Program.cs</c> (unhandled, since it ran before <c>WithStdioServerTransport().RunAsync()</c>)
/// crashed this entire MCP server the moment the host wasn't already listening — live-reproduced via
/// every live scenario test failing at startup with no host process
/// running for this repo's own orchestrator-level test harness to talk to.
/// </summary>
public sealed class HubConnectionStarter(HubConnection hubConnection, ILogger<HubConnectionStarter> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await hubConnection.StartAsync(stoppingToken);
                logger.LogInformation("Connected to the host chat hub after {Attempts} attempt(s).", attempt);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Attempt {Attempt} to connect to the host chat hub failed; retrying in {DelaySeconds}s.",
                    attempt, RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}

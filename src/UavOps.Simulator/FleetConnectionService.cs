using UavOps.FleetClient;

namespace UavOps.Simulator;

/// <summary>
/// Keeps a <see cref="FleetClientConnection"/> to UavOps.Agent open, the way the real fleet app
/// does. The host may not be up yet (or may restart), so connecting retries forever instead of
/// failing startup; the client's own automatic reconnect covers short drops, and this loop starts
/// a fresh connection once that gives up.
/// </summary>
public sealed class FleetConnectionService(
    SimulatorCommandHandler handler,
    SimOptions options,
    ILogger<FleetConnectionService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private volatile FleetClientConnection? _connection;
    private volatile bool _connected;

    public bool IsConnected => _connected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loggedWaiting = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new FleetClientConnection(options.HostHubUrl, handler);
            connection.Connected += id => { _connected = true; logger.LogInformation("Connected to {Url} ({ConnectionId}).", options.HostHubUrl, id); };
            connection.Reconnecting += _ => { _connected = false; logger.LogWarning("Connection to the host lost; reconnecting..."); };
            connection.Disconnected += _ => { _connected = false; closed.TrySetResult(); };

            try
            {
                await connection.StartAsync();
                _connection = connection;
                loggedWaiting = false;
                await closed.Task.WaitAsync(stoppingToken);
                logger.LogWarning("Disconnected from the host; connecting again.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await connection.StopAsync();
                return;
            }
            catch (Exception ex)
            {
                if (!loggedWaiting)
                {
                    logger.LogInformation("Waiting for UavOps.Agent at {Url} ({Error}); retrying every {Seconds}s.",
                        options.HostHubUrl, ex.Message, RetryDelay.TotalSeconds);
                    loggedWaiting = true;
                }
            }

            _connected = false;
            _connection = null;
            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public Task ReportDetectionAsync(DetectionReport report) =>
        SendAsync(c => c.ReportDetectionAsync(report), $"detection of '{report.Prompt}' by {report.TailNumber}");

    public Task ReportMissionEventAsync(MissionEventReport report) =>
        SendAsync(c => c.ReportMissionEventAsync(report), $"{report.TailNumber} mission {report.Kind}");

    private async Task SendAsync(Func<FleetClientConnection, Task> send, string what)
    {
        var connection = _connection;
        if (connection is null || !_connected)
        {
            logger.LogWarning("Not connected to the host; dropped {What}.", what);
            return;
        }

        try
        {
            await send(connection);
            logger.LogInformation("Reported {What}.", what);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Reporting {What} failed: {Error}", what, ex.Message);
        }
    }
}

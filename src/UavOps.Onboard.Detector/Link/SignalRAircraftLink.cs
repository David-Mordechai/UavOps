using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector.Autonomy;

namespace UavOps.Onboard.Detector.Link;

/// <summary>The "Link" section: how the onboard computer talks to its aircraft.</summary>
public sealed class AircraftLinkOptions
{
    public const string SectionName = "Link";

    /// <summary><c>SignalR</c>: dial the ground agent's onboard hub (<see cref="HubUrl"/>) and take
    /// tasks and send reports over it - no open port needed here. <c>Http</c>: the aircraft calls this
    /// service's <c>/tasks</c> and gets reports on its callback URLs (a local dev run).</summary>
    public string Mode { get; set; } = "Http";

    /// <summary>The ground agent's onboard hub, e.g. <c>http://192.168.1.157:5262/onboardHub</c>.</summary>
    public string HubUrl { get; set; } = "";

    /// <summary>The tails this onboard computer serves (comma-separated), or <c>*</c> for all.</summary>
    public string Tails { get; set; } = "*";

    public bool UsesSignalR => Mode.Equals("SignalR", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The onboard computer's link to its aircraft over SignalR (<see cref="OnboardLink"/>): connects out
/// to the ground agent's onboard hub and keeps connecting (the agent may be down, or restart); takes
/// search tasks from it; sends detections, target reports and payload commands back; and pushes
/// every search's status every couple of seconds (how the aircraft knows this computer is there and
/// how far each search has got). The camera video doesn't go this way - it's read from the aircraft
/// directly (<see cref="SearchTask.VideoSourceUrl"/>).
/// </summary>
public sealed class SignalRAircraftLink(
    AircraftLinkOptions options,
    IServiceProvider services,
    ILogger<SignalRAircraftLink> logger) : BackgroundService, IDetectionSink, ITrackSink, IPayloadControl
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);
    private volatile HubConnection? _connection;

    public bool Connected => _connection?.State == HubConnectionState.Connected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = $"{options.HubUrl}{(options.HubUrl.Contains('?') ? '&' : '?')}{OnboardLink.RoleQuery}={OnboardLink.Onboard}" +
                  $"&{OnboardLink.TailsQuery}={Uri.EscapeDataString(options.Tails)}";
        var loggedWaiting = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var connection = new HubConnectionBuilder().WithUrl(url).WithAutomaticReconnect().Build();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            connection.On<string, string, JsonElement>(OnboardLink.Receive, Receive);
            try
            {
                await connection.StartAsync(stoppingToken);
                _connection = connection;
                loggedWaiting = false;
                logger.LogInformation("Connected to the ground agent's onboard hub at {Url}.", options.HubUrl);
                await PushStatusUntilClosedAsync(connection, closed.Task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!loggedWaiting)
                {
                    logger.LogInformation("Waiting for the ground agent at {Url} ({Error}); retrying every {Seconds} s.",
                        options.HubUrl, ex.Message, RetryDelay.TotalSeconds);
                    loggedWaiting = true;
                }
            }
            _connection = null;
            await connection.DisposeAsync();
            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PushStatusUntilClosedAsync(HubConnection connection, Task closed, CancellationToken stoppingToken)
    {
        var registry = services.GetRequiredService<SearchTaskRegistry>();
        while (!closed.IsCompleted && !stoppingToken.IsCancellationRequested)
        {
            if (connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await SendAsync("*", OnboardLink.Kinds.Status, registry.Statuses(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug("Status push failed: {Message}", ex.Message);
                }
            }
            await Task.WhenAny(closed, Task.Delay(StatusInterval, stoppingToken));
        }
    }

    private void Receive(string tail, string kind, JsonElement payload)
    {
        var registry = services.GetRequiredService<SearchTaskRegistry>();
        switch (kind)
        {
            case OnboardLink.Kinds.TaskStart when payload.Deserialize<SearchTask>(Json) is { } task:
                logger.LogInformation("Task for {Tail} from the aircraft: '{Prompt}'{Track}.", tail, task.Prompt, task.Track ? ", find and track" : "");
                registry.Start(task);
                break;
            case OnboardLink.Kinds.TaskStop:
                registry.Stop(tail);
                break;
            default:
                logger.LogWarning("Unknown message '{Kind}' for {Tail} from the aircraft.", kind, tail);
                break;
        }
    }

    private async Task SendAsync(string tail, string kind, object? payload, CancellationToken cancellationToken)
    {
        if (_connection is not { State: HubConnectionState.Connected } connection)
            throw new InvalidOperationException("Not connected to the ground agent.");
        await connection.InvokeAsync(OnboardLink.ToAircraft, tail, kind, JsonSerializer.SerializeToElement(payload, Json), cancellationToken);
    }

    // The callback URLs are the HTTP link's; over SignalR the message's tail says where it goes.
    public Task SendAsync(string callbackUrl, OnboardDetection detection, CancellationToken cancellationToken) =>
        SendAsync(detection.TailNumber, OnboardLink.Kinds.Detection, detection, cancellationToken);

    public Task SendAsync(string callbackUrl, TargetTrackReport report, CancellationToken cancellationToken) =>
        SendAsync(report.TailNumber, OnboardLink.Kinds.Track, report, cancellationToken);

    public Task PointAtAsync(SearchTask task, double lat, double lng, CancellationToken cancellationToken) =>
        SendAsync(task.TailNumber, OnboardLink.Kinds.PayloadPoint, new PointAtCommand(lat, lng), cancellationToken);

    public Task ZoomAsync(SearchTask task, double groundWidthMeters, CancellationToken cancellationToken) =>
        SendAsync(task.TailNumber, OnboardLink.Kinds.PayloadZoom, new ZoomCommand(groundWidthMeters), cancellationToken);

    public Task ReleaseAsync(SearchTask task, CancellationToken cancellationToken) =>
        SendAsync(task.TailNumber, OnboardLink.Kinds.PayloadRelease, null, cancellationToken);
}

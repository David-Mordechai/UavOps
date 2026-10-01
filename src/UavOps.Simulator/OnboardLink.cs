using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using UavOps.Onboard.Contracts;

namespace UavOps.Simulator;

/// <summary>
/// How the aircraft talks to its onboard computer: hand it a search task or take it back, and know
/// whether it's there and how far each search has got. What comes back (detections, target reports,
/// payload commands) is delivered to <see cref="OnboardDetectorClient"/> and <see cref="SimFleet"/>
/// by the link. The camera video isn't part of it - the onboard computer reads it from this app
/// directly (<see cref="SearchTask.VideoSourceUrl"/>).
/// </summary>
public interface IOnboardLink
{
    /// <summary>The onboard computer answered recently.</summary>
    bool Reachable { get; }
    IReadOnlyList<SearchTaskStatus> Statuses { get; }
    string Describe { get; }
    Task StartAsync(SearchTask task, CancellationToken cancellationToken);
    Task StopAsync(string tail, CancellationToken cancellationToken);
}

/// <summary>
/// Over the ground agent's onboard hub (<see cref="OnboardLink"/>, SignalR): this app connects out
/// as the aircraft, the onboard computer connects out too, and the agent routes messages between
/// them by tail. Neither needs to reach the other directly, so the onboard computer needs no open
/// port. The onboard computer's status arrives every couple of seconds; without it for
/// <see cref="StatusTimeout"/> it's taken as unreachable.
/// </summary>
public sealed class SignalROnboardLink(
    SimOptions options,
    IServiceProvider services,
    ILogger<SignalROnboardLink> logger) : BackgroundService, IOnboardLink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private volatile HubConnection? _connection;
    private volatile IReadOnlyList<SearchTaskStatus> _statuses = [];
    private DateTime _lastStatusUtc = DateTime.MinValue;

    public string HubUrl => options.OnboardHubUrl;
    public bool Reachable => _connection?.State == HubConnectionState.Connected && DateTime.UtcNow - _lastStatusUtc < StatusTimeout;
    public IReadOnlyList<SearchTaskStatus> Statuses => _statuses;
    public string Describe => $"the onboard computer via {HubUrl}";

    public Task StartAsync(SearchTask task, CancellationToken cancellationToken) =>
        SendAsync(task.TailNumber, OnboardLink.Kinds.TaskStart, task, cancellationToken);

    public Task StopAsync(string tail, CancellationToken cancellationToken) =>
        SendAsync(tail, OnboardLink.Kinds.TaskStop, null, cancellationToken);

    private async Task SendAsync(string tail, string kind, object? payload, CancellationToken cancellationToken)
    {
        // Without a recent status nobody would receive it: fail, so the caller tries again later.
        if (_connection is not { State: HubConnectionState.Connected } connection || !Reachable)
            throw new InvalidOperationException("The onboard computer isn't connected to the ground agent.");
        await connection.InvokeAsync(OnboardLink.ToOnboard, tail, kind, JsonSerializer.SerializeToElement(payload, Json), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.UsesOnboardDetector)
            return;
        var url = $"{HubUrl}?{OnboardLink.RoleQuery}={OnboardLink.Aircraft}";
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
                logger.LogInformation("Onboard link connected at {Url}.", HubUrl);
                await closed.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!loggedWaiting)
                {
                    logger.LogInformation("Waiting for the ground agent's onboard hub at {Url} ({Error}).", HubUrl, ex.Message);
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

    private void Receive(string tail, string kind, JsonElement payload)
    {
        try
        {
            var onboard = services.GetRequiredService<OnboardDetectorClient>();
            var fleet = services.GetRequiredService<SimFleet>();
            switch (kind)
            {
                case OnboardLink.Kinds.Status:
                    _statuses = payload.Deserialize<List<SearchTaskStatus>>(Json) ?? [];
                    _lastStatusUtc = DateTime.UtcNow;
                    break;
                case OnboardLink.Kinds.Detection when payload.Deserialize<OnboardDetection>(Json) is { } detection:
                    onboard.Receive(detection);
                    break;
                case OnboardLink.Kinds.Track when payload.Deserialize<TargetTrackReport>(Json) is { } report:
                    onboard.ReceiveTrack(report);
                    break;
                case OnboardLink.Kinds.PayloadPoint when payload.Deserialize<PointAtCommand>(Json) is { } point:
                    fleet.OnboardPoint(tail, point.Lat, point.Lng);
                    break;
                case OnboardLink.Kinds.PayloadZoom when payload.Deserialize<ZoomCommand>(Json) is { } zoom:
                    fleet.OnboardZoom(tail, zoom.GroundWidthMeters);
                    break;
                case OnboardLink.Kinds.PayloadRelease:
                    fleet.OnboardRelease(tail);
                    break;
                default:
                    logger.LogWarning("Unknown message '{Kind}' for {Tail} from the onboard computer.", kind, tail);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Couldn't handle '{Kind}' for {Tail} from the onboard computer: {Message}", kind, tail, ex.Message);
        }
    }
}

/// <summary>
/// Plain HTTP both ways (a local dev run, or an onboard computer this app can reach): tasks with
/// PUT/DELETE <c>{OnboardUrl}/tasks/{tail}</c>, status by polling <c>/tasks</c>; reports and payload
/// commands come in on this app's own endpoints (<c>/api/onboard/*</c>, <c>/api/uavs/{tail}/payload/*</c>).
/// </summary>
public sealed class HttpOnboardLink(HttpClient http, SimOptions options) : BackgroundService, IOnboardLink
{
    private volatile IReadOnlyList<SearchTaskStatus> _statuses = [];

    public bool Reachable { get; private set; }
    public IReadOnlyList<SearchTaskStatus> Statuses => _statuses;
    public string Describe => options.OnboardUrl;
    private string BaseUrl => options.OnboardUrl.TrimEnd('/');

    public async Task StartAsync(SearchTask task, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"{BaseUrl}/tasks/{Uri.EscapeDataString(task.TailNumber)}", task, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopAsync(string tail, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"{BaseUrl}/tasks/{Uri.EscapeDataString(tail)}", cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.UsesOnboardDetector)
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _statuses = await http.GetFromJsonAsync<List<SearchTaskStatus>>($"{BaseUrl}/tasks", stoppingToken) ?? [];
                Reachable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                Reachable = false;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

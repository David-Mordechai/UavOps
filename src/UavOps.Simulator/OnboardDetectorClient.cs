using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Threading.Channels;
using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Onboard.Contracts;
using UavOps.Simulator.Camera;

namespace UavOps.Simulator;

/// <summary>A detection the onboard service reported, with where its box was, for the page.</summary>
public sealed record OnboardDetectionView(
    string TailNumber, string MissionId, string Label, double Confidence, double Lat, double Lng,
    long FrameSeq, BoundingBox Box, long ModelLatencyMs, double WidthMeters, double HeightMeters, DateTime DetectedAtUtc);

/// <summary>
/// The aircraft's side of the onboard detection service (UavOps.Onboard.Detector, the Jetson
/// process next to the camera). When a UAV starts looking for something it sends the service a
/// <see cref="SearchTask"/> (PUT /tasks/{tail}), and a DELETE when it stops; the service pulls the
/// survey frames (<see cref="SurveyFrameBuffer"/>) itself and POSTs back each
/// <see cref="OnboardDetection"/>, which <see cref="Look"/> hands to the tick as an ordinary
/// <see cref="DetectionReport"/> - so reporting to the host is unchanged.
///
/// <see cref="Look"/> runs on the tick under the fleet lock, so every call to the service happens
/// on a background loop; a service that's down is retried until the search ends.
/// </summary>
public sealed class OnboardDetectorClient(
    HttpClient http,
    SimOptions options,
    SurveyFrameBuffer frames,
    ILogger<OnboardDetectorClient> logger) : BackgroundService, IOnboardDetector
{
    private readonly ConcurrentDictionary<string, string> _activeSearch = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<OnboardDetection>> _received = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Tail, SearchTask? Task)> _commands = Channel.CreateUnbounded<(string, SearchTask?)>();
    private readonly ConcurrentQueue<OnboardDetectionView> _recent = new();
    private readonly ConcurrentDictionary<string, (long Through, DateTime Since)> _progress = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<SearchTaskStatus> _statuses = [];

    /// <summary>How long a finished route waits on a detector that has stopped making progress
    /// before the search is reported complete anyway.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    public bool Reachable { get; private set; }
    public IReadOnlyList<SearchTaskStatus> Statuses => _statuses;
    public IReadOnlyList<OnboardDetectionView> Recent => _recent.ToList();

    public IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc)
    {
        var key = uav.IsLooking ? $"{uav.MissionId}\n{uav.SearchPrompt}" : null;
        _activeSearch.TryGetValue(uav.TailNumber, out var current);
        if (key != current)
        {
            if (key is null)
            {
                _activeSearch.TryRemove(uav.TailNumber, out _);
                _commands.Writer.TryWrite((uav.TailNumber, null));
            }
            else
            {
                _activeSearch[uav.TailNumber] = key;
                var baseUrl = options.PublicBaseUrl.TrimEnd('/');
                _commands.Writer.TryWrite((uav.TailNumber, new SearchTask(
                    uav.TailNumber, uav.MissionId!, uav.ZoneName ?? "", uav.SearchPrompt!, uav.MinConfidence,
                    $"{baseUrl}/api/uavs/{Uri.EscapeDataString(uav.TailNumber)}/frames",
                    uav.LastFrameSeq,
                    $"{baseUrl}/api/onboard/detections",
                    $"{baseUrl}/api/uavs/{Uri.EscapeDataString(uav.TailNumber)}/zoom")));
            }
        }

        if (!_received.TryGetValue(uav.TailNumber, out var queue))
            yield break;
        while (queue.TryDequeue(out var d))
        {
            // A detection from a search that has since ended or changed is dropped.
            if (d.MissionId != uav.MissionId || !uav.IsLooking)
                continue;
            yield return new DetectionReport
            {
                TailNumber = d.TailNumber,
                MissionId = d.MissionId,
                ZoneName = d.ZoneName,
                Prompt = d.Prompt,
                Label = d.Label,
                Confidence = Math.Round(d.Confidence, 2),
                Lat = Math.Round(d.Lat, 6),
                Lng = Math.Round(d.Lng, 6),
                DetectedAtUtc = d.DetectedAtUtc,
                TrackId = d.TrackId
            };
        }
    }

    /// <summary>
    /// Done when the service reports every frame up to the camera's last one analysed. A service
    /// that can't be reached, or that has made no progress for <see cref="StallTimeout"/>, doesn't
    /// hold the mission open forever: the search is reported complete with what was found.
    /// </summary>
    public bool HasFinished(SimUav uav)
    {
        if (!Reachable)
        {
            logger.LogWarning("Onboard detector unreachable; completing {Tail}'s search without waiting for its frames.", uav.TailNumber);
            return true;
        }
        var status = _statuses.FirstOrDefault(s => string.Equals(s.TailNumber, uav.TailNumber, StringComparison.OrdinalIgnoreCase) && s.MissionId == uav.MissionId);
        if (status is not null && status.AnalyzedThroughSeq >= uav.LastFrameSeq)
            return true;

        var through = status?.AnalyzedThroughSeq ?? -1;
        var progress = _progress.AddOrUpdate(uav.TailNumber, (through, DateTime.UtcNow),
            (_, p) => p.Through == through ? p : (through, DateTime.UtcNow));
        if (DateTime.UtcNow - progress.Since > StallTimeout)
        {
            logger.LogWarning("Onboard detector made no progress on {Tail}'s frames for {Minutes} min; completing the search.",
                uav.TailNumber, StallTimeout.TotalMinutes);
            _progress.TryRemove(uav.TailNumber, out _);
            return true;
        }
        return false;
    }

    /// <summary>The service's callback (POST /api/onboard/detections).</summary>
    public void Receive(OnboardDetection detection)
    {
        _received.GetOrAdd(detection.TailNumber, _ => new ConcurrentQueue<OnboardDetection>()).Enqueue(detection);

        // Box size on the ground, so the live view can draw it wherever the object is in frame.
        var (width, height) = (8.0, 8.0);
        if (frames.Get(detection.TailNumber, detection.FrameSeq) is { } frame)
        {
            var camera = new CameraModel(frame.Telemetry);
            width = (detection.Box.X2 - detection.Box.X1) / 1000 * camera.GroundWidthMeters;
            height = (detection.Box.Y2 - detection.Box.Y1) / 1000 * camera.GroundHeightMeters;
        }
        _recent.Enqueue(new OnboardDetectionView(detection.TailNumber, detection.MissionId, detection.Label, detection.Confidence,
            detection.Lat, detection.Lng, detection.FrameSeq, detection.Box, detection.ModelLatencyMs, width, height, detection.DetectedAtUtc));
        while (_recent.Count > 50)
            _recent.TryDequeue(out _);
        logger.LogInformation("Onboard detection on {Tail}: {Label} ({Confidence:P0}) at {Lat:F5}, {Lng:F5} in frame {Seq}, model {Ms} ms.",
            detection.TailNumber, detection.Label, detection.Confidence, detection.Lat, detection.Lng, detection.FrameSeq, detection.ModelLatencyMs);
    }

    /// <summary>Boxes for the live view: this UAV's detections in its current search.</summary>
    public List<CameraOverlay> OverlaysFor(string tail, string? missionId) =>
        missionId is null
            ? []
            : _recent.Where(d => string.Equals(d.TailNumber, tail, StringComparison.OrdinalIgnoreCase) && d.MissionId == missionId)
                .Select(d => new CameraOverlay(new GeoPoint(d.Lat, d.Lng), d.WidthMeters, d.HeightMeters, $"{d.Label} {d.Confidence:0.00}"))
                .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.UsesOnboardDetector)
            return;
        var baseUrl = options.OnboardDetectorUrl.TrimEnd('/');
        var pending = new Dictionary<string, SearchTask?>(StringComparer.OrdinalIgnoreCase);
        var nextStatus = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_commands.Reader.TryRead(out var command))
                pending[command.Tail] = command.Task;

            foreach (var (tail, task) in pending.ToList())
            {
                try
                {
                    using var response = task is null
                        ? await http.DeleteAsync($"{baseUrl}/tasks/{Uri.EscapeDataString(tail)}", stoppingToken)
                        : await http.PutAsJsonAsync($"{baseUrl}/tasks/{Uri.EscapeDataString(tail)}", task, stoppingToken);
                    if (response.IsSuccessStatusCode || (task is null && response.StatusCode == System.Net.HttpStatusCode.NotFound))
                    {
                        pending.Remove(tail);
                        logger.LogInformation(task is null ? "Onboard search on {Tail} stopped." : "Onboard search on {Tail} started: '{Prompt}'.",
                            tail, task?.Prompt);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Onboard detector at {Url} unreachable ({Message}); retrying.", baseUrl, ex.Message);
                }
            }

            if (DateTime.UtcNow >= nextStatus)
            {
                nextStatus = DateTime.UtcNow.AddSeconds(2);
                try
                {
                    _statuses = await http.GetFromJsonAsync<List<SearchTaskStatus>>($"{baseUrl}/tasks", stoppingToken) ?? [];
                    Reachable = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    Reachable = false;
                }
            }

            try
            {
                await Task.Delay(pending.Count > 0 ? 2000 : 250, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

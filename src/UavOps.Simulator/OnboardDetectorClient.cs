using System.Collections.Concurrent;
using System.Threading.Channels;
using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Onboard.Contracts;
using UavOps.Simulator.Camera;

namespace UavOps.Simulator;

/// <summary>A detection the onboard service reported, with where its box was, for the page.</summary>
public sealed record OnboardDetectionView(
    string TailNumber, string MissionId, string Label, double Confidence, double Lat, double Lng,
    long FrameSeq, BoundingBox Box, long ModelLatencyMs, double WidthMeters, double HeightMeters, DateTime DetectedAtUtc,
    long? SnapshotId = null);

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
    IOnboardLink link,
    SimOptions options,
    SurveyFrameBuffer frames,
    VideoFrameBuffer video,
    ILogger<OnboardDetectorClient> logger) : BackgroundService, IOnboardDetector
{
    /// <summary>The frame each recent detection was made in, kept when the detection arrives: the
    /// video buffer only holds a few seconds, and the page asks for the picture later than that.</summary>
    private readonly ConcurrentDictionary<long, byte[]> _snapshots = new();
    private long _nextSnapshot;
    private readonly ConcurrentDictionary<string, SearchTask> _activeTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeSearch = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<OnboardDetection>> _received = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TargetTrackReport>> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Tail, SearchTask? Task)> _commands = Channel.CreateUnbounded<(string, SearchTask?)>();
    private readonly ConcurrentQueue<OnboardDetectionView> _recent = new();
    private readonly ConcurrentDictionary<string, (long Through, DateTime Since)> _progress = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a finished route waits on a detector that has stopped making progress
    /// before the search is reported complete anyway.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    public bool Reachable => link.Reachable;
    public IReadOnlyList<SearchTaskStatus> Statuses => link.Statuses;
    public string LinkDescription => link.Describe;
    public IReadOnlyList<OnboardDetectionView> Recent => _recent.ToList();

    public IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc)
    {
        var key = uav.IsLooking ? $"{uav.MissionId}\n{uav.SearchPrompt}\n{uav.TrackTarget}" : null;
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
                var uavUrl = $"{baseUrl}/api/uavs/{Uri.EscapeDataString(uav.TailNumber)}";
                _commands.Writer.TryWrite((uav.TailNumber, new SearchTask(
                    uav.TailNumber, uav.MissionId!, uav.ZoneName ?? "", uav.SearchPrompt!, uav.MinConfidence,
                    $"{uavUrl}/frames",
                    uav.LastFrameSeq,
                    $"{baseUrl}/api/onboard/detections",
                    $"{uavUrl}/zoom",
                    Track: uav.TrackTarget,
                    VideoSourceUrl: $"{uavUrl}/video",
                    PayloadUrl: $"{uavUrl}/payload",
                    TrackCallbackUrl: $"{baseUrl}/api/onboard/tracks")));
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
        var status = link.Statuses.FirstOrDefault(s => string.Equals(s.TailNumber, uav.TailNumber, StringComparison.OrdinalIgnoreCase) && s.MissionId == uav.MissionId);
        if (status is not null && status.AnalyzedThroughSeq >= uav.LastFrameSeq)
            return true;
        // The every-frame pipeline works on the live video, never a backlog: done once any check
        // of a candidate in hand has an answer.
        if (status?.Phase is { } phase)
            return phase != "Verifying";

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

        // Box size on the ground, so the live view can draw it wherever the object is in frame; and
        // the frame itself, kept for the page (a detection's frame number is a live-video frame's
        // when it comes from the every-frame pipeline, a survey frame's otherwise).
        var (width, height) = (8.0, 8.0);
        long? snapshot = null;
        var frame = detection.FromVideo ? video.Get(detection.TailNumber, detection.FrameSeq) : frames.Get(detection.TailNumber, detection.FrameSeq);
        if (frame is not null)
        {
            var camera = new CameraModel(frame.Telemetry);
            width = (detection.Box.X2 - detection.Box.X1) / 1000 * camera.GroundWidthMeters;
            height = (detection.Box.Y2 - detection.Box.Y1) / 1000 * camera.GroundHeightMeters;
            snapshot = Interlocked.Increment(ref _nextSnapshot);
            _snapshots[snapshot.Value] = frame.Jpeg;
        }
        _recent.Enqueue(new OnboardDetectionView(detection.TailNumber, detection.MissionId, detection.Label, detection.Confidence,
            detection.Lat, detection.Lng, detection.FrameSeq, detection.Box, detection.ModelLatencyMs, width, height, detection.DetectedAtUtc,
            snapshot));
        while (_recent.Count > 50)
            if (_recent.TryDequeue(out var old) && old.SnapshotId is { } gone)
                _snapshots.TryRemove(gone, out _);
        logger.LogInformation("Onboard detection on {Tail}: {Label} ({Confidence:P0}) at {Lat:F5}, {Lng:F5} in frame {Seq}, model {Ms} ms.",
            detection.TailNumber, detection.Label, detection.Confidence, detection.Lat, detection.Lng, detection.FrameSeq, detection.ModelLatencyMs);
    }

    /// <summary>The onboard computer's report about a target it tracks (POST /api/onboard/tracks).</summary>
    public void ReceiveTrack(TargetTrackReport report)
    {
        _tracks.GetOrAdd(report.TailNumber, _ => new ConcurrentQueue<TargetTrackReport>()).Enqueue(report);
        if (report.State != TargetTrackStates.Tracking)
            logger.LogInformation("Onboard track on {Tail}: {Track} ({Label}) {State} at {Lat:F5}, {Lng:F5}.",
                report.TailNumber, report.TrackId, report.Label, report.State, report.Lat, report.Lng);
    }

    /// <summary>A recent detection's frame (see <see cref="OnboardDetectionView.SnapshotId"/>).</summary>
    public byte[]? Snapshot(long id) => _snapshots.TryGetValue(id, out var jpeg) ? jpeg : null;

    public TargetTrackReport? TakeTrack(SimUav uav) =>
        _tracks.TryGetValue(uav.TailNumber, out var queue) && queue.TryDequeue(out var report) ? report : null;

    /// <summary>Boxes for the live view: this UAV's detections in its current search.</summary>
    public List<CameraOverlay> OverlaysFor(string tail, string? missionId) =>
        missionId is null
            ? []
            : _recent.Where(d => string.Equals(d.TailNumber, tail, StringComparison.OrdinalIgnoreCase) && d.MissionId == missionId)
                .Select(d => new CameraOverlay(new GeoPoint(d.Lat, d.Lng), d.WidthMeters, d.HeightMeters, $"{d.Label} {d.Confidence:0.00}"))
                .ToList();

    /// <summary>
    /// Hands each UAV's task to the onboard computer, or takes it back, through the link; anything
    /// that couldn't be sent is retried. A search the onboard computer doesn't have (it restarted, or
    /// connected after the search began) is sent again.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.UsesOnboardDetector)
            return;
        var pending = new Dictionary<string, SearchTask?>(StringComparer.OrdinalIgnoreCase);
        var nextResync = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_commands.Reader.TryRead(out var command))
            {
                pending[command.Tail] = command.Task;
                if (command.Task is null)
                    _activeTasks.TryRemove(command.Tail, out _);
                else
                    _activeTasks[command.Tail] = command.Task;
            }

            if (DateTime.UtcNow >= nextResync && link.Reachable)
            {
                nextResync = DateTime.UtcNow.AddSeconds(5);
                foreach (var (tail, task) in _activeTasks)
                    if (!pending.ContainsKey(tail) && !link.Statuses.Any(s => string.Equals(s.TailNumber, tail, StringComparison.OrdinalIgnoreCase) && s.MissionId == task.MissionId))
                    {
                        logger.LogInformation("The onboard computer has no task for {Tail}'s search; sending it again.", tail);
                        pending[tail] = task;
                        // It starts that search over, with no target: a UAV still circling a target it
                        // was tracking goes back to its route (the same as the onboard computer giving
                        // the target up), instead of circling a stale position for ever.
                        ReceiveTrack(new TargetTrackReport(tail, task.MissionId, task.ZoneName, task.Prompt, "", "",
                            TargetTrackStates.Released, 0, 0, 0, null, 0, DateTime.UtcNow));
                    }
            }

            foreach (var (tail, task) in pending.ToList())
            {
                try
                {
                    if (task is null)
                        await link.StopAsync(tail, stoppingToken);
                    else
                        await link.StartAsync(task, stoppingToken);
                    pending.Remove(tail);
                    logger.LogInformation(task is null ? "Onboard search on {Tail} stopped." : "Onboard search on {Tail} started: '{Prompt}'.", tail, task?.Prompt);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug("Couldn't reach {Link} for {Tail} ({Message}); retrying.", link.Describe, tail, ex.Message);
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

using System.Collections.Concurrent;
using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector;

/// <summary>
/// Runs one UAV's search: pulls every camera frame in order, has the vision model look at each
/// (up to <see cref="DetectorOptions.MaxConcurrentFrames"/> frames at once, see
/// <see cref="AnalyzeAsync"/>), turns each match's box into a ground position
/// (<see cref="CameraModel"/>, from that frame's own telemetry), and reports each object once
/// (<see cref="DetectionTracker"/>). A frame the model fails on is logged and skipped - the next
/// one overlaps it.
/// </summary>
public sealed class SearchTaskRunner(
    SearchTask task,
    IFrameSource frames,
    IVisionModel model,
    IDetectionSink sink,
    DetectionTracker tracker,
    DetectorOptions options,
    ILogger logger,
    IZoomCamera? zoom = null)
{
    private readonly ConcurrentDictionary<long, byte> _inFlight = new();
    private readonly List<(GeoPoint Position, ObjectDescription Description, bool IsTarget)> _examined = [];
    private readonly bool _vehicle = TargetKind.IsVehicle(task.Prompt);
    private readonly ConcurrentDictionary<string, Task<bool>> _meansCache = new(StringComparer.Ordinal);
    private Task<string>? _meant;
    private long _lastPulledSeq;
    private int _framesAnalyzed;
    private int _detections;
    private long _lastLatencyMs;
    private string? _lastError;

    public SearchTask Search => task;

    public SearchTaskStatus Status => new(task.TailNumber, task.MissionId, task.Prompt,
        AnalyzedThroughSeq, _framesAnalyzed, _detections, Interlocked.Read(ref _lastLatencyMs), _lastError);

    /// <summary>Frames are pulled in order and analysed a few at a time: everything before the
    /// oldest one still in flight is done.</summary>
    private long AnalyzedThroughSeq
    {
        get
        {
            var pulled = Interlocked.Read(ref _lastPulledSeq);
            var inFlight = _inFlight.Keys.ToList();
            return inFlight.Count == 0 ? pulled : Math.Min(pulled, inFlight.Min() - 1);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Searching {Tail}'s camera for '{Prompt}' (mission {Mission}, frames after {From}).",
            task.TailNumber, task.Prompt, task.MissionId, task.FromSeq);
        using var slots = new SemaphoreSlim(Math.Max(options.MaxConcurrentFrames, 1));
        var inFlight = new ConcurrentDictionary<long, Task>();
        var after = task.FromSeq;
        Interlocked.Exchange(ref _lastPulledSeq, after);

        while (!cancellationToken.IsCancellationRequested)
        {
            CameraFrame? frame;
            try
            {
                frame = await frames.NextAsync(task.FrameSourceUrl, after, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = $"Frame source: {ex.Message}";
                logger.LogWarning("Couldn't get a frame for {Tail} from {Url}: {Message}", task.TailNumber, task.FrameSourceUrl, ex.Message);
                await Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
            if (frame is null)
                continue;
            after = frame.Telemetry.Seq;
            if (frame.Telemetry.MissionId is { } missionId && missionId != task.MissionId)
            {
                Interlocked.Exchange(ref _lastPulledSeq, after);
                continue;
            }

            try
            {
                await slots.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _inFlight[after] = 0;
            Interlocked.Exchange(ref _lastPulledSeq, after);
            var work = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await AnalyzeAsync(frame, cancellationToken);
                }
                finally
                {
                    _inFlight.TryRemove(frame.Telemetry.Seq, out _);
                    slots.Release();
                    inFlight.TryRemove(frame.Telemetry.Seq, out _);
                }
            }, CancellationToken.None);
            inFlight[frame.Telemetry.Seq] = work;
        }

        await System.Threading.Tasks.Task.WhenAll(inFlight.Values);
        logger.LogInformation("Stopped searching {Tail}'s camera for '{Prompt}': {Frames} frames, {Detections} objects.",
            task.TailNumber, task.Prompt, _framesAnalyzed, _detections);
    }

    /// <summary>
    /// One frame, in two questions (see <see cref="DetectionPrompt"/>): which objects in the frame
    /// could be the target, then, for each, what a close-up of it actually is. Only a candidate whose
    /// description is the target (<see cref="IsTargetAsync"/>) is placed on the ground and, if it's
    /// new, reported. A structure needs a second look at the other close-up width to say so too
    /// (<see cref="ConfirmStructureAsync"/>).
    /// </summary>
    public async Task AnalyzeAsync(CameraFrame frame, CancellationToken cancellationToken)
    {
        var seq = frame.Telemetry.Seq;
        List<FrameHit> candidates;
        long latency;
        try
        {
            var question = _vehicle
                ? DetectionPrompt.Candidates(task.Prompt)
                : DetectionPrompt.StructureCandidates(task.Prompt, await MeantAsync(cancellationToken));
            var answer = await model.AskAsync(frame.Jpeg, question, cancellationToken);
            candidates = DetectionParser.Parse(answer.Text, minConfidence: 0).Take(options.MaxCandidatesPerFrame).ToList();
            latency = answer.LatencyMs;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _lastError = $"Model: {ex.Message}";
            logger.LogWarning("Vision model failed on {Tail} frame {Seq}: {Message}", task.TailNumber, seq, ex.Message);
            return;
        }

        var camera = new CameraModel(frame.Telemetry);
        var checks = await Task.WhenAll(candidates.Select(async candidate =>
        {
            try
            {
                // Seen up close already (overlapping frames): same answer, no second look.
                var position = camera.NormalizedToGeo(candidate.Box.CenterX, candidate.Box.CenterY);
                if (Examined(position) is { } known)
                    return (candidate, description: (ObjectDescription?)known.Description, isTarget: known.IsTarget, LatencyMs: 0L);

                var (closeUp, metersAcross) = await CloseUpAsync(frame, camera, candidate, null, cancellationToken);
                var answer = await model.AskAsync(closeUp,
                    _vehicle ? DetectionPrompt.Describe(metersAcross) : DetectionPrompt.DescribeObject(metersAcross), cancellationToken);
                var description = _vehicle ? DetectionParser.ParseDescription(answer.Text) : DetectionParser.ParseWhat(answer.Text);
                var latency = answer.LatencyMs;
                var isTarget = description is not null && description.Confidence >= task.MinConfidence &&
                               await IsTargetAsync(description, cancellationToken);
                if (isTarget && !_vehicle)
                {
                    var (confirmed, confirmLatency) = await ConfirmStructureAsync(frame, camera, candidate, metersAcross, cancellationToken);
                    isTarget = confirmed;
                    latency += confirmLatency;
                }
                if (description is not null)
                    lock (_examined)
                        _examined.Add((position, description, isTarget));
                return (candidate, description, isTarget, LatencyMs: latency);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Close-up check failed on {Tail} frame {Seq}: {Message}", task.TailNumber, seq, ex.Message);
                return (candidate, description: (ObjectDescription?)null, isTarget: false, LatencyMs: 0L);
            }
        }));
        latency += checks.Length == 0 ? 0 : checks.Max(c => c.LatencyMs);

        Interlocked.Increment(ref _framesAnalyzed);
        Interlocked.Exchange(ref _lastLatencyMs, latency);
        _lastError = null;
        logger.LogInformation("{Tail} frame {Seq}: {Candidates} candidate(s) [{Descriptions}] in {Ms} ms.", task.TailNumber, seq, candidates.Count,
            string.Join(", ", checks.Select(c => c.description?.Text ?? "?")), latency);

        foreach (var (candidate, description, isTarget, _) in checks)
        {
            if (!isTarget || description is null)
                continue;

            var position = camera.NormalizedToGeo(candidate.Box.CenterX, candidate.Box.CenterY);
            var track = tracker.Observe(task.MissionId, task.Prompt, position, description.Confidence);
            if (track is null)
                continue;

            Interlocked.Increment(ref _detections);
            var detection = new OnboardDetection(task.TailNumber, task.MissionId, task.ZoneName, task.Prompt, description.Text,
                description.Confidence, position.Lat, position.Lng, DateTime.UtcNow, track.Id, seq, candidate.Box, latency);
            logger.LogInformation("Found '{Label}' ({Confidence:P0}) for {Tail} at {Lat:F5}, {Lng:F5} in frame {Seq} ({Ms} ms).",
                description.Text, description.Confidence, task.TailNumber, position.Lat, position.Lng, seq, latency);
            try
            {
                await sink.SendAsync(task.DetectionCallbackUrl, detection, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _lastError = $"Callback: {ex.Message}";
                logger.LogWarning("Couldn't report detection {Track} to {Url}: {Message}", track.Id, task.DetectionCallbackUrl, ex.Message);
            }
        }
    }

    /// <summary>
    /// A second look at a structure that the first close-up said is the target, at the other end of
    /// the structure close-up range (30 m if the first was wider, else 40 m): it's the target only if
    /// this one says so too. Measured on ZoneB's real photo: the false hits changed their answer
    /// between the two widths (a drainage channel called a road bridge at 40 m, bare cables called an
    /// electricity pylon at 40 m and a power line at 30 m, a highway a bridge at 30 m and a sign gantry
    /// at 40 m), while both pylons, the mast and the bridge gave the same answer at both.
    /// </summary>
    private async Task<(bool Confirmed, long LatencyMs)> ConfirmStructureAsync(
        CameraFrame frame, CameraModel camera, FrameHit candidate, double firstMetersAcross, CancellationToken cancellationToken)
    {
        var middle = (options.MinStructureZoomWidthMeters + options.MaxStructureZoomWidthMeters) / 2;
        var width = firstMetersAcross > middle ? options.MinStructureZoomWidthMeters : options.MaxStructureZoomWidthMeters;
        var (closeUp, metersAcross) = await CloseUpAsync(frame, camera, candidate, width, cancellationToken);
        var answer = await model.AskAsync(closeUp, DetectionPrompt.DescribeObject(metersAcross), cancellationToken);
        var second = DetectionParser.ParseWhat(answer.Text);
        var confirmed = second is not null && second.Confidence >= task.MinConfidence && await IsTargetAsync(second, cancellationToken);
        if (!confirmed)
            logger.LogInformation("{Tail}: second look at {Meters:F0} m saw '{Second}', not the target; dropped.",
                task.TailNumber, metersAcross, second?.Text ?? "?");
        return (confirmed, answer.LatencyMs);
    }

    /// <summary>A vehicle by its words (colour and type); anything else by asking the model whether
    /// what was seen is what the operator means, once per distinct name.</summary>
    private async Task<bool> IsTargetAsync(ObjectDescription description, CancellationToken cancellationToken)
    {
        if (_vehicle)
            return TargetMatcher.Matches(task.Prompt, description.Text);
        // No word-match shortcut here: "bridge" is in "road leading onto a bridge".
        var meant = await MeantAsync(cancellationToken);
        return await _meansCache.GetOrAdd(description.Text, async seen =>
        {
            try
            {
                var answer = await model.AskTextAsync(DetectionPrompt.SameKind(task.Prompt, meant, seen), cancellationToken);
                return DetectionParser.ParseField(answer.Text, "match") == "true";
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Couldn't compare '{Seen}' with '{Prompt}': {Message}", seen, task.Prompt, ex.Message);
                _meansCache.TryRemove(seen, out _);
                return false;
            }
        });
    }

    /// <summary>What the operator most likely means by a non-vehicle target, asked once per search;
    /// their own words if the model can't be asked.</summary>
    private Task<string> MeantAsync(CancellationToken cancellationToken)
    {
        if (_meant is { IsCompletedSuccessfully: true } or { IsCompleted: false })
            return _meant;
        return _meant = InterpretAsync(cancellationToken);
    }

    private async Task<string> InterpretAsync(CancellationToken cancellationToken)
    {
        try
        {
            var answer = await model.AskTextAsync(DetectionPrompt.Interpret(task.Prompt), cancellationToken);
            var meant = DetectionParser.ParseField(answer.Text, "object");
            if (!string.IsNullOrWhiteSpace(meant))
            {
                logger.LogInformation("{Tail}'s search for '{Prompt}' looks for: {Meant}.", task.TailNumber, task.Prompt, meant);
                return meant;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't interpret '{Prompt}': {Message}", task.Prompt, ex.Message);
        }
        return task.Prompt;
    }

    private (ObjectDescription Description, bool IsTarget)? Examined(GeoPoint position)
    {
        lock (_examined)
        {
            foreach (var e in _examined)
                if (GeoProjection.DistanceMeters(e.Position, position) <= options.SameObjectMeters)
                    return (e.Description, e.IsTarget);
            return null;
        }
    }

    /// <summary>
    /// A close-up of a candidate: the payload zooms onto it (a real close-up, as sharp as the
    /// payload allows) when the aircraft offers a zoom; otherwise, or if the zoom can't reach it,
    /// the candidate is cropped out of the survey frame and scaled up.
    /// </summary>
    private async Task<(byte[] Jpeg, double MetersAcross)> CloseUpAsync(
        CameraFrame frame, CameraModel camera, FrameHit candidate, double? metersAcross, CancellationToken cancellationToken)
    {
        if (zoom is not null && task.ZoomUrl is { } zoomUrl)
        {
            var box = candidate.Box;
            var sizeMeters = Math.Max((box.X2 - box.X1) / 1000 * camera.GroundWidthMeters, (box.Y2 - box.Y1) / 1000 * camera.GroundHeightMeters);
            var width = metersAcross ?? (_vehicle
                ? Math.Clamp(sizeMeters * 2.5, options.MinZoomWidthMeters, options.MaxZoomWidthMeters)
                : Math.Clamp(sizeMeters * 1.5, options.MinStructureZoomWidthMeters, options.MaxStructureZoomWidthMeters));
            var center = camera.NormalizedToGeo(box.CenterX, box.CenterY);
            try
            {
                if (await zoom.CaptureAsync(zoomUrl, center.Lat, center.Lng, width, options.ZoomPixels, cancellationToken) is { } jpeg)
                    return (jpeg, width);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Zoom unavailable for {Tail} ({Message}); cropping the frame instead.", task.TailNumber, ex.Message);
            }
        }
        var crop = FrameCropper.Crop(frame.Jpeg, candidate.Box, camera.MetersPerPixel, out var croppedMeters);
        return (crop, croppedMeters);
    }

    private static async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>The searches running now, one per UAV: a new task for a UAV replaces its old one.</summary>
public sealed class SearchTaskRegistry(
    IFrameSource frames,
    IVisionModel model,
    IDetectionSink sink,
    IZoomCamera zoom,
    DetectorOptions options,
    ILoggerFactory loggers) : IHostedService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (SearchTaskRunner Runner, CancellationTokenSource Stop, Task Running)> _running =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DetectionTracker _tracker = new(options.TrackRadiusMeters);

    public void Start(SearchTask task)
    {
        Stop(task.TailNumber);
        var runner = new SearchTaskRunner(task, frames, model, sink, _tracker, options, loggers.CreateLogger<SearchTaskRunner>(), zoom);
        var stop = new CancellationTokenSource();
        var running = Task.Run(() => runner.RunAsync(stop.Token));
        lock (_lock)
            _running[task.TailNumber] = (runner, stop, running);
    }

    public bool Stop(string tail)
    {
        (SearchTaskRunner, CancellationTokenSource Stop, Task)? entry = null;
        lock (_lock)
        {
            if (_running.Remove(tail, out var found))
                entry = found;
        }
        if (entry is null)
            return false;
        entry.Value.Stop.Cancel();
        entry.Value.Stop.Dispose();
        return true;
    }

    public List<SearchTaskStatus> Statuses()
    {
        lock (_lock)
            return _running.Values.Select(r => r.Runner.Status).ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Task> running;
        lock (_lock)
            running = _running.Values.Select(r => r.Running).ToList();
        foreach (var tail in _running.Keys.ToList())
            Stop(tail);
        await Task.WhenAll(running).WaitAsync(cancellationToken);
    }
}

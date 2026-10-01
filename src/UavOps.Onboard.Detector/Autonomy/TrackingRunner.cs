using System.Diagnostics;
using SkiaSharp;
using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector.Perception;

namespace UavOps.Onboard.Detector.Autonomy;

/// <summary>Where the mission executive is.</summary>
public static class ExecutivePhase
{
    public const string Searching = "Searching";
    public const string Verifying = "Verifying";
    public const string Tracking = "Tracking";
    public const string Reacquiring = "Reacquiring";
}

/// <summary>
/// The onboard autonomy loop for one UAV - perception, then a mission executive, then payload
/// actions - built to run on a Jetson Orin Nano in near real time:
/// <list type="number">
/// <item>Pull the newest video frame (frames that piled up meanwhile are skipped: this loop runs
/// on the present, not a backlog).</item>
/// <item>The fast detector finds every vehicle and person (<see cref="IObjectDetector"/>, tens of ms).</item>
/// <item>Each is placed on the ground with the frame's telemetry, its colour read from its pixels,
/// and tracked (<see cref="GroundTracker"/>: ids, positions, velocities).</item>
/// <item>The executive (<see cref="Step"/>) decides: a confirmed track that could be the target
/// (<see cref="TargetSpec"/>: class and colour) gets one zoomed close-up checked by the verifier
/// (<see cref="IVerifier"/>, a small VLM, seconds - off the loop, one at a time). A verified target
/// is reported found; in a find-and-track mission the executive then locks the payload on it,
/// keeps it centred and zoomed, and reports where it is (<see cref="TargetTrackReport"/>) about
/// once a second. Lost, it looks where the target should be, and takes back a new track there
/// only after the verifier says it's the target again.</item>
/// </list>
/// No language model decides anything here: the executive is plain code, testable and the same
/// every time. The verifier only answers "what is this?".
/// </summary>
public sealed class TrackingRunner(
    SearchTask task,
    TargetSpec spec,
    IObjectDetector detector,
    IFrameSource frames,
    IZoomCamera zoom,
    IVerifier verifier,
    IPayloadControl payload,
    ITrackSink trackSink,
    IDetectionSink detectionSink,
    PerceptionOptions options,
    ILogger logger,
    Func<DateTime>? clock = null) : ISearchRunner
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly object _lock = new();
    private GroundTracker? _tracker;
    private string _phase = ExecutivePhase.Searching;
    private Track? _target;
    private Track? _verifying;
    private Task? _verification;
    private DateTime _lostAtUtc;
    private LostTargetSearch? _lostSearch;
    private DateTime _lookSinceUtc;
    private GeoPoint? _look;
    private double? _searchGroundWidthMeters;
    private DateTime _lastReportUtc;
    private DateTime _lastPointUtc;
    private string? _lastReportedState;
    private GeoPoint? _lastPointed;
    private readonly Dictionary<int, GeoPoint> _reportedFound = [];
    private long _lastSeq;
    private int _frames;
    private int _found;
    private long _lastVerifyMs;
    private string? _lastError;
    private readonly Rolling _fetch = new(), _decode = new(), _detect = new(), _track = new(), _interval = new();
    private DateTime _lastFrameAt;

    public SearchTask Search => task;
    public string Phase { get { lock (_lock) return _phase; } }
    public Track? Target { get { lock (_lock) return _target; } }

    /// <summary>The close-up check in progress, if any (tests wait on it).</summary>
    public Task Verification { get { lock (_lock) return _verification ?? Task.CompletedTask; } }

    public SearchTaskStatus Status
    {
        get
        {
            lock (_lock)
                return new SearchTaskStatus(task.TailNumber, task.MissionId, task.Prompt, _lastSeq, _frames, _found, _lastVerifyMs, _lastError,
                    _verification is { IsCompleted: false } && _phase == ExecutivePhase.Searching ? ExecutivePhase.Verifying : _phase,
                    _target?.Label,
                    new PipelineTiming(_fetch.Mean, _decode.Mean, _detect.Mean, _track.Mean,
                        _interval.Mean > 0 ? Math.Round(1000 / _interval.Mean, 1) : 0, _tracker?.Tracks.Count ?? 0, detector.Name));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var source = task.VideoSourceUrl ?? task.FrameSourceUrl;
        logger.LogInformation("{Tail}: {Mode} for '{Prompt}' with {Detector} (classes {Classes}, colours {Colours}), video {Source}.",
            task.TailNumber, task.Track ? "find and track" : "search", task.Prompt, detector.Name,
            string.Join("/", spec.Classes), spec.Colours.Count == 0 ? "any" : string.Join("/", spec.Colours), source);
        var after = task.VideoSourceUrl is null ? task.FromSeq : 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var fetch = Stopwatch.StartNew();
                CameraFrame? frame;
                try
                {
                    frame = await frames.NextAsync(source, after, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _lastError = $"Frame source: {ex.Message}";
                    logger.LogWarning("{Tail}: no frame from {Url}: {Message}", task.TailNumber, source, ex.Message);
                    await Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }
                if (frame is null)
                    continue;
                after = frame.Telemetry.Seq;
                if (frame.Telemetry.MissionId is { } missionId && missionId != task.MissionId)
                    continue;
                _fetch.Add(fetch.Elapsed.TotalMilliseconds);
                try
                {
                    await ProcessAsync(frame, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _lastError = ex.Message;
                    logger.LogWarning(ex, "{Tail}: frame {Seq} failed.", task.TailNumber, frame.Telemetry.Seq);
                }
            }
        }
        finally
        {
            if (task.Track && _target is not null)
                await Try(() => payload.ReleaseAsync(task, CancellationToken.None), "release the payload");
            logger.LogInformation("{Tail}: stopped ({Frames} frames, {Found} found).", task.TailNumber, _frames, _found);
        }
    }

    /// <summary>Perception on one frame, then the executive. Public for tests.</summary>
    public async Task ProcessAsync(CameraFrame frame, CancellationToken cancellationToken)
    {
        var t = frame.Telemetry;
        var watch = Stopwatch.StartNew();
        using var image = FrameDecoder.Decode(frame.Jpeg, detector.InputSize) ?? throw new InvalidDataException("The frame isn't a readable image.");
        _decode.Add(watch.Elapsed.TotalMilliseconds);

        watch.Restart();
        var objects = detector.Detect(image, options.MinScore);
        _detect.Add(watch.Elapsed.TotalMilliseconds);

        watch.Restart();
        var camera = new CameraModel(t);
        var sigma = Math.Max(options.MinPositionSigmaMeters, camera.MetersPerPixel * 4);
        var observations = objects
            .Where(o => ObjectClasses.IsTracked(o.Class))
            .Select(o =>
            {
                var size = Math.Max((o.Box.X2 - o.Box.X1) / 1000 * camera.GroundWidthMeters, (o.Box.Y2 - o.Box.Y1) / 1000 * camera.GroundHeightMeters);
                return new GroundObservation(camera.NormalizedToGeo(o.Box.CenterX, o.Box.CenterY), o.Class, o.Score,
                    ColourNamer.Name(image, o.Box), o.Box, size, sigma);
            })
            // Implausible sizes are the detector reading texture as a car.
            .Where(o => o.SizeMeters is > 1.2 and < 25)
            .ToList();

        lock (_lock)
        {
            _tracker ??= new GroundTracker(new GeoPoint(t.Lat, t.Lng))
            {
                HighScore = options.HighScore,
                CoastSeconds = options.CoastSeconds,
                MemorySeconds = options.TrackMemorySeconds
            };
            var touched = _tracker.Update(observations, t.CapturedAtUtc, t.Seq, camera.Contains);
            // Every candidate that just became confirmed: what the detector saw, for the log.
            foreach (var track in touched.Where(tr => tr.State == TrackState.Confirmed && tr.Hits == GroundTracker.ConfirmHits))
                if (spec.CouldBe(track.Class, track.Colour))
                    logger.LogInformation("{Tail}: candidate {Track} confirmed ({Class}, {Colour}, score {Score:F2}) in frame {Seq}.",
                        task.TailNumber, track.Label, track.Class, track.Colour, track.Last.Score, t.Seq);
            _lastSeq = t.Seq;
            _frames++;
        }
        _track.Add(watch.Elapsed.TotalMilliseconds);
        var now = _now();
        if (_lastFrameAt != default)
            _interval.Add((now - _lastFrameAt).TotalMilliseconds);
        _lastFrameAt = now;

        await Step(frame, cancellationToken);
    }

    /// <summary>The mission executive: one decision per frame.</summary>
    private async Task Step(CameraFrame frame, CancellationToken cancellationToken)
    {
        var tracker = _tracker!;
        var t = frame.Telemetry;
        switch (Phase)
        {
            case ExecutivePhase.Searching:
                StartVerificationIfIdle(frame, candidate: null, cancellationToken);
                if (!task.Track)
                    await ReportMovedFoundAsync(cancellationToken);
                break;

            case ExecutivePhase.Tracking:
            {
                var target = _target!;
                if (target.State == TrackState.Lost)
                {
                    lock (_lock)
                    {
                        _phase = ExecutivePhase.Reacquiring;
                        _lostAtUtc = t.CapturedAtUtc;
                        _lostSearch = new LostTargetSearch(tracker.PositionOf(target), target.VelocityLocal, target.LastSeenUtc,
                            options.ReacquireRadiusMeters, options.MinAssumedSpeedMps, options.MaxPredictSeconds, options.ReacquireGroundWidthMeters);
                        _look = null;
                    }
                    logger.LogInformation("{Tail}: lost {Track}; searching around where it should be.", task.TailNumber, target.Label);
                    await ReportAsync(target, TargetTrackStates.Lost, _lostSearch.Center(t.CapturedAtUtc), cancellationToken, force: true);
                    await ZoomAsync(options.ReacquireGroundWidthMeters, cancellationToken);
                    break;
                }
                var predicted = tracker.Predict(target, t.CapturedAtUtc + TimeSpan.FromMilliseconds(options.PointLeadMs));
                await PointAsync(predicted, t.CapturedAtUtc, cancellationToken);
                await ReportAsync(target, target.State == TrackState.Coasting ? TargetTrackStates.Coasting : TargetTrackStates.Tracking,
                    tracker.PositionOf(target), cancellationToken);
                break;
            }

            case ExecutivePhase.Reacquiring:
            {
                var target = _target!;
                var search = _lostSearch!;
                var now = t.CapturedAtUtc;
                if ((now - _lostAtUtc).TotalSeconds > options.ReacquireSeconds)
                {
                    await GiveUpAsync(target, now, cancellationToken);
                    break;
                }
                // Step the payload through the search pattern, holding each look long enough for
                // the tracker to confirm what's in it.
                if (_look is null || (now - _lookSinceUtc).TotalSeconds >= options.LookDwellSeconds)
                {
                    _look = search.NextLook(now);
                    _lookSinceUtc = now;
                    await PointAsync(_look.Value, now, cancellationToken, force: true);
                }
                // The aircraft circles the search area as it moves (Lost reports carry its centre).
                if ((now - _lastReportUtc).TotalSeconds >= options.LostReportSeconds)
                    await ReportAsync(target, TargetTrackStates.Lost, search.Center(now), cancellationToken, force: true);
                var center = search.Center(now);
                var radius = search.RadiusMeters(now);
                var candidate = tracker.Tracks
                    .Where(c => c != target && c.State == TrackState.Confirmed && c.IsTarget != false && spec.CouldStillBe(c.Class, c.Colour))
                    .OrderBy(c => GeoProjection.DistanceMeters(tracker.PositionOf(c), center))
                    .FirstOrDefault(c => GeoProjection.DistanceMeters(tracker.PositionOf(c), center) <= radius);
                if (candidate is not null)
                    StartVerificationIfIdle(frame, candidate, cancellationToken);
                break;
            }
        }
    }

    /// <summary>One close-up check at a time (the VLM is the slow part): a given candidate
    /// (reacquisition), or the best unverified confirmed track that could be the target.</summary>
    private void StartVerificationIfIdle(CameraFrame frame, Track? candidate, CancellationToken cancellationToken)
    {
        var tracker = _tracker!;
        lock (_lock)
        {
            if (_verification is { IsCompleted: false })
                return;
            candidate ??= tracker.Tracks
                .Where(c => c.State == TrackState.Confirmed && c.IsTarget is null && c.LastFrameSeq == frame.Telemetry.Seq && spec.CouldBe(c.Class, c.Colour))
                .OrderByDescending(c => c.Last.Score)
                .FirstOrDefault();
            if (candidate is null)
                return;
            _verifying = candidate;
            var reacquiring = _phase == ExecutivePhase.Reacquiring;
            var at = tracker.PositionOf(candidate);
            var size = candidate.Last.SizeMeters;
            _verification = Task.Run(() => VerifyAsync(frame, candidate, at, size, reacquiring, cancellationToken), CancellationToken.None);
        }
    }

    private async Task VerifyAsync(CameraFrame frame, Track candidate, GeoPoint at, double sizeMeters, bool reacquiring, CancellationToken cancellationToken)
    {
        try
        {
            var width = Math.Clamp(sizeMeters * 2.5, options.MinCloseUpMeters, options.MaxCloseUpMeters);
            // The video's own zoom: its ?seq= is a video frame number.
            var zoomUrl = task.VideoSourceUrl is { } video ? video.TrimEnd('/') + "/zoom" : task.ZoomUrl;
            var closeUp = zoomUrl is not null
                ? await zoom.CaptureAsync(zoomUrl, at.Lat, at.Lng, width, options.CloseUpPixels, cancellationToken, frame.Telemetry.Seq)
                : null;
            if (closeUp is null)
            {
                var camera = new CameraModel(frame.Telemetry);
                closeUp = FrameCropper.Crop(frame.Jpeg, candidate.Last.Box, camera.MetersPerPixel, out width);
            }
            var verdict = await verifier.VerifyAsync(closeUp, width, task.Prompt, cancellationToken);
            Interlocked.Exchange(ref _lastVerifyMs, verdict.LatencyMs);
            logger.LogInformation("{Tail}: {Track} ({Class}, {Colour}) is '{Description}' - {Verdict} ({Ms} ms).", task.TailNumber, candidate.Label,
                candidate.Class, candidate.Colour ?? "?", verdict.Description ?? "?", verdict.IsTarget ? "the target" : "not the target", verdict.LatencyMs);

            lock (_lock)
            {
                candidate.Description = verdict.Description;
                candidate.IsTarget = verdict.IsTarget;
            }
            if (!verdict.IsTarget)
                return;

            if (reacquiring)
            {
                var target = _target!;
                lock (_lock)
                {
                    _tracker!.Merge(target, candidate);
                    target.Accepts = o => spec.CouldStillBe(o.Class, o.Colour);
                    _phase = ExecutivePhase.Tracking;
                }
                logger.LogInformation("{Tail}: found {Track} again.", task.TailNumber, target.Label);
                await ZoomAsync(options.TrackGroundWidthMeters, cancellationToken);
                await ReportAsync(target, TargetTrackStates.Tracking, _tracker.PositionOf(target), cancellationToken, force: true);
                return;
            }

            Interlocked.Increment(ref _found);
            await ReportFoundAsync(candidate, frame, cancellationToken);
            if (task.Track && Phase == ExecutivePhase.Searching)
            {
                lock (_lock)
                {
                    _target = candidate;
                    // From now on only something that could be the target continues it.
                    candidate.Accepts = o => spec.CouldStillBe(o.Class, o.Colour);
                    _phase = ExecutivePhase.Tracking;
                    // The search zoom, to go back to if the target is given up.
                    _searchGroundWidthMeters ??= new CameraModel(frame.Telemetry).GroundWidthMeters;
                }
                logger.LogInformation("{Tail}: locking on {Track} ({Description}).", task.TailNumber, candidate.Label, verdict.Description);
                // The report first: it's what switches the aircraft from its route to following.
                await ReportAsync(candidate, TargetTrackStates.Tracking, _tracker!.PositionOf(candidate), cancellationToken, force: true);
                await PointAsync(_tracker.PositionOf(candidate), frame.Telemetry.CapturedAtUtc, cancellationToken, force: true);
                await ZoomAsync(options.TrackGroundWidthMeters, cancellationToken);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _lastError = $"Verifier: {ex.Message}";
            logger.LogWarning("{Tail}: couldn't verify {Track}: {Message}", task.TailNumber, candidate.Label, ex.Message);
        }
        finally
        {
            lock (_lock)
                _verifying = null;
        }
    }

    /// <summary>
    /// Not found again within <see cref="PerceptionOptions.ReacquireSeconds"/>: the target is handed
    /// back (a <see cref="TargetTrackStates.Released"/> report - the aircraft resumes its search
    /// route), the payload goes back to straight down at the search zoom, and the executive searches
    /// again. The old track stays known as the target, so seeing it again costs no second check.
    /// </summary>
    private async Task GiveUpAsync(Track target, DateTime now, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Tail}: couldn't find {Track} again in {Seconds:F0} s; back to searching.",
            task.TailNumber, target.Label, (now - _lostAtUtc).TotalSeconds);
        await ReportAsync(target, TargetTrackStates.Released, _lostSearch!.Center(now), cancellationToken, force: true);
        lock (_lock)
        {
            _phase = ExecutivePhase.Searching;
            _target = null;
            _lostSearch = null;
            _look = null;
            _lastPointed = null;
        }
        await Try(() => payload.ReleaseAsync(task, cancellationToken), "release the payload");
        if (_searchGroundWidthMeters is { } width)
            await ZoomAsync(width, cancellationToken);
    }

    /// <summary>"Found": the ordinary detection report, so the operator hears about it exactly as
    /// from a search today (and "send 998 to the red car" works), now with the track's id.</summary>
    private async Task ReportFoundAsync(Track track, CameraFrame frame, CancellationToken cancellationToken)
    {
        var at = _tracker!.PositionOf(track);
        lock (_lock)
            _reportedFound[track.Id] = at;
        var detection = new OnboardDetection(task.TailNumber, task.MissionId, task.ZoneName, task.Prompt,
            track.Description ?? $"{track.Colour} {track.Class}".Trim(), Math.Round(track.Last.Score, 2), at.Lat, at.Lng,
            track.LastSeenUtc, track.Label, track.LastFrameSeq, track.Last.Box, _lastVerifyMs, FromVideo: true);
        await Try(() => detectionSink.SendAsync(task.DetectionCallbackUrl, detection, cancellationToken), $"report {track.Label}");
    }

    /// <summary>Search only (no tracking): a verified target that has driven on is reported again
    /// under its id, as the old pipeline did.</summary>
    private async Task ReportMovedFoundAsync(CancellationToken cancellationToken)
    {
        List<Track> moved;
        lock (_lock)
            moved = _tracker!.Tracks.Where(tr => tr.IsTarget == true && tr.State == TrackState.Confirmed && _reportedFound.TryGetValue(tr.Id, out var last)
                && GeoProjection.DistanceMeters(last, _tracker.PositionOf(tr)) > options.MoveReportMeters).ToList();
        foreach (var track in moved)
        {
            var at = _tracker!.PositionOf(track);
            lock (_lock)
                _reportedFound[track.Id] = at;
            var detection = new OnboardDetection(task.TailNumber, task.MissionId, task.ZoneName, task.Prompt,
                track.Description ?? track.Class, Math.Round(track.Last.Score, 2), at.Lat, at.Lng, track.LastSeenUtc, track.Label,
                track.LastFrameSeq, track.Last.Box, 0, FromVideo: true);
            await Try(() => detectionSink.SendAsync(task.DetectionCallbackUrl, detection, cancellationToken), $"report {track.Label} moved");
        }
    }

    /// <summary>The target's position to the aircraft: on a state change at once, otherwise at
    /// most every <see cref="PerceptionOptions.ReportIntervalSeconds"/>.</summary>
    private async Task ReportAsync(Track target, string state, GeoPoint at, CancellationToken cancellationToken, bool force = false)
    {
        var url = task.TrackCallbackUrl ?? "";
        var now = _now();
        if (!force && state == _lastReportedState && (now - _lastReportUtc).TotalSeconds < options.ReportIntervalSeconds)
            return;
        _lastReportUtc = now;
        _lastReportedState = state;
        var report = new TargetTrackReport(task.TailNumber, task.MissionId, task.ZoneName, task.Prompt, target.Label,
            target.Description ?? $"{target.Colour} {target.Class}".Trim(), state, Math.Round(at.Lat, 6), Math.Round(at.Lng, 6),
            Math.Round(target.SpeedMps, 1), target.HeadingDeg is { } h ? Math.Round(h) : null, Math.Round(target.Last.Score, 2), target.LastSeenUtc);
        await Try(() => trackSink.SendAsync(url, report, cancellationToken), $"report {target.Label} {state}");
    }

    /// <summary>Keep the gimbal on a point: re-aimed when the point has moved more than a few metres
    /// or every <see cref="PerceptionOptions.PointIntervalMs"/>, not every frame for nothing.</summary>
    private async Task PointAsync(GeoPoint at, DateTime frameUtc, CancellationToken cancellationToken, bool force = false)
    {
        var moved = _lastPointed is not { } last || GeoProjection.DistanceMeters(last, at) > options.PointDeadbandMeters;
        if (!force && !moved && (_now() - _lastPointUtc).TotalMilliseconds < options.PointIntervalMs)
            return;
        _lastPointed = at;
        _lastPointUtc = _now();
        await Try(() => payload.PointAtAsync(task, at.Lat, at.Lng, cancellationToken), "point the payload");
    }

    private Task ZoomAsync(double groundWidthMeters, CancellationToken cancellationToken) =>
        Try(() => payload.ZoomAsync(task, groundWidthMeters, cancellationToken), "zoom");

    private async Task Try(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = $"Couldn't {what}: {ex.Message}";
            logger.LogWarning("{Tail}: couldn't {What}: {Message}", task.TailNumber, what, ex.Message);
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A running mean of the last few samples.</summary>
    private sealed class Rolling
    {
        private readonly Queue<double> _samples = new();
        private double _sum;

        public double Mean
        {
            get
            {
                lock (_samples)
                    return _samples.Count == 0 ? 0 : Math.Round(_sum / _samples.Count, 1);
            }
        }

        public void Add(double value)
        {
            lock (_samples)
            {
                _samples.Enqueue(value);
                _sum += value;
                if (_samples.Count > 30)
                    _sum -= _samples.Dequeue();
            }
        }
    }
}

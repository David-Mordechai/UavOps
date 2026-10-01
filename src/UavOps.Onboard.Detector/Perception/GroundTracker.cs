using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector.Perception;

public enum TrackState { Tentative, Confirmed, Coasting, Lost }

/// <summary>One detection placed on the ground, ready for the tracker.</summary>
public sealed record GroundObservation(
    GeoPoint Position, string Class, double Score, string? Colour, BoundingBox Box, double SizeMeters, double SigmaMeters);

/// <summary>
/// An object the tracker keeps: position and velocity from a constant-velocity Kalman filter in
/// local metres (east, north), its class and colour by vote, and the executive's verdict on it.
/// Only touched under <see cref="GroundTracker"/>'s caller's lock.
/// </summary>
public sealed class Track
{
    private readonly Dictionary<string, double> _classVotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _colourVotes = new(StringComparer.Ordinal);

    internal Track(int id, Kalman2D filter, GroundObservation first, DateTime seenAtUtc, long frameSeq)
    {
        Id = id;
        Filter = filter;
        FirstSeenUtc = LastSeenUtc = LastUpdateUtc = seenAtUtc;
        Observe(first, seenAtUtc, frameSeq);
    }

    public int Id { get; }
    public string Label => $"T-{Id}";
    internal Kalman2D Filter { get; }
    public TrackState State { get; internal set; } = TrackState.Tentative;
    public int Hits { get; private set; }
    public DateTime FirstSeenUtc { get; }
    public DateTime LastSeenUtc { get; private set; }
    internal DateTime LastUpdateUtc { get; set; }

    /// <summary>How long the camera has looked where this track should be without seeing it, since
    /// it was last seen - what a coasting track is lost by.</summary>
    public double MissedInViewSeconds { get; internal set; }

    internal double LastFrameDtSeconds { get; set; }
    public long LastFrameSeq { get; private set; }
    public GroundObservation Last { get; private set; } = null!;

    public string Class => _classVotes.MaxBy(v => v.Value).Key;
    public string? Colour => _colourVotes.Count == 0 ? null : _colourVotes.MaxBy(v => v.Value).Key;

    /// <summary>What the vision model said this is, once asked (null until then), and whether that
    /// matched the target.</summary>
    public string? Description { get; set; }
    public bool? IsTarget { get; set; }

    /// <summary>Which sightings may continue this track; null: any compatible one. Set on the locked
    /// target, so it's only ever continued by something that could be the target - and set, it
    /// marks the track as locked (<see cref="IsLocked"/>).</summary>
    public Func<GroundObservation, bool>? Accepts { get; set; }

    /// <summary>The executive's locked target: matched first, and keeps its sightings
    /// (see <see cref="GroundTracker"/>).</summary>
    public bool IsLocked => Accepts is not null;

    public Vec2 PositionLocal => new(Filter.X[0], Filter.X[1]);
    public Vec2 VelocityLocal => new(Filter.X[2], Filter.X[3]);
    public double SpeedMps => VelocityLocal.Length;

    /// <summary>Compass heading of motion; null when (nearly) still.</summary>
    public double? HeadingDeg => SpeedMps < 1 ? null : (Math.Atan2(VelocityLocal.X, VelocityLocal.Y) * 180 / Math.PI + 360) % 360;

    internal void Observe(GroundObservation o, DateTime seenAtUtc, long frameSeq)
    {
        Hits++;
        MissedInViewSeconds = 0;
        LastSeenUtc = seenAtUtc;
        LastFrameSeq = frameSeq;
        Last = o;
        _classVotes[o.Class] = _classVotes.GetValueOrDefault(o.Class) + o.Score;
        if (o.Colour is not null)
            _colourVotes[o.Colour] = _colourVotes.GetValueOrDefault(o.Colour) + o.Score;
    }
}

/// <summary>
/// Multi-object tracking on the ground plane. Frames come with their telemetry, so every detection
/// is placed on the ground first (<see cref="CameraModel"/>) and tracked there, where a parked car
/// stands still however the aircraft moves - no image-space motion compensation needed.
///
/// Per frame, ByteTrack-style: predict every track to the frame's time; match confident
/// detections first, then weak ones to the tracks still unmatched (a weak detection only
/// continues a track, never starts one); the Kalman filter's own uncertainty gates each match
/// (Mahalanobis, 99%), so a fast car is looked for further away than a parked one. A track is
/// confirmed after <see cref="ConfirmHits"/> hits, not necessarily in a row: a new track survives
/// misses for <see cref="TentativeSeconds"/> (measured: a small car from 4,000 ft is detected in
/// about half the frames, and dropping a new track at its first miss meant the red car was seen
/// over and over during a pass and never once confirmed). A confirmed track missed while in view coasts on
/// its prediction and is lost after <see cref="CoastSeconds"/>; out of view it isn't counted as
/// missed at all (the camera moved on, the car didn't vanish). The coast is counted in time spent
/// looking (<see cref="Track.MissedInViewSeconds"/>), not since last seen: the locked target is last
/// seen before its 2-3 s close-up check and the payload's slew, and counted from then it was lost
/// about a second after the camera first looked for it (measured on the Jetson, with a reacquired
/// target lost two frames after it was found again). Lost tracks are forgotten after
/// <see cref="MemorySeconds"/>.
///
/// The locked target (<see cref="Track.IsLocked"/>) is never out-competed for its own sightings:
/// it is matched before any other track, with a wider gate (99.9%), and a track started from what
/// can only be its sightings is folded back into it. Measured on the Jetson without this: a
/// sighting just outside the 99% gate (d² 9.5) started a second track on the same car, which then
/// took every later sighting - one 1.3 m from the target's prediction - while the target coasted
/// on and was declared lost with the car in plain view.
/// </summary>
public sealed class GroundTracker(GeoPoint origin)
{
    public const int ConfirmHits = 3;
    public double HighScore { get; init; } = 0.5;
    public double CoastSeconds { get; init; } = 4;

    /// <summary>The fastest anything tracked can move (m/s): the velocity estimate is capped to it,
    /// and a sighting further from a track than it could have got since last seen (plus
    /// <see cref="ReachMarginMeters"/>) never continues it, however uncertain the filter has grown.
    /// Measured without these: a locked car's track slid 300 m away onto other vehicles while still
    /// "tracking", its speed estimate running away and its gate growing with it.</summary>
    public double MaxSpeedMps { get; init; } = 35;
    public double ReachMarginMeters { get; init; } = 8;
    public double TentativeSeconds { get; init; } = 1.5;
    public double MemorySeconds { get; init; } = 60;
    public double AccelerationSigma { get; init; } = 3;
    public double MissedBeforeAbsorbSeconds { get; init; } = 1;

    private readonly GeoProjection _projection = new(origin);
    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    public GeoProjection Projection => _projection;
    public IReadOnlyList<Track> Tracks => _tracks;

    public GeoPoint PositionOf(Track track) => _projection.ToGeo(track.PositionLocal);

    /// <summary>Where the track should be at <paramref name="atUtc"/> (constant velocity).</summary>
    public GeoPoint Predict(Track track, DateTime atUtc)
    {
        var dt = Math.Max((atUtc - track.LastUpdateUtc).TotalSeconds, 0);
        return _projection.ToGeo(track.PositionLocal + track.VelocityLocal * dt);
    }

    /// <summary>One frame's observations; <paramref name="inView"/> says whether a ground point was in
    /// this frame (so a track outside it isn't counted as missed). Returns the tracks matched or
    /// started this frame.</summary>
    public List<Track> Update(IReadOnlyList<GroundObservation> observations, DateTime frameUtc, long frameSeq, Func<GeoPoint, bool> inView)
    {
        foreach (var track in _tracks)
        {
            var dt = (frameUtc - track.LastUpdateUtc).TotalSeconds;
            track.LastFrameDtSeconds = Math.Max(dt, 0);
            if (dt > 0)
            {
                track.Filter.Predict(dt, AccelerationSigma);
                track.LastUpdateUtc = frameUtc;
            }
        }

        var local = observations.Select(o => _projection.ToLocal(o.Position)).ToList();
        var unmatchedTracks = _tracks.Where(t => t.State != TrackState.Lost).ToHashSet();
        var unmatchedObs = Enumerable.Range(0, observations.Count).ToHashSet();
        var touched = new List<Track>();

        void Associate(IEnumerable<int> candidates)
        {
            var pairs = new List<(double Cost, Track Track, int Obs)>();
            foreach (var i in candidates)
            foreach (var track in unmatchedTracks)
            {
                var d2 = track.Filter.MahalanobisSquared(local[i], observations[i].SigmaMeters);
                var reach = ReachMarginMeters + MaxSpeedMps * Math.Max((frameUtc - track.LastSeenUtc).TotalSeconds, 0);
                if (d2 <= Kalman2D.Gate99 && ClassesCompatible(track.Class, observations[i].Class)
                    && (local[i] - track.PositionLocal).Length <= reach
                    && (track.Accepts?.Invoke(observations[i]) ?? true))
                    pairs.Add((d2 + (track.Colour is { } c && observations[i].Colour is { } oc && c != oc ? 2 : 0), track, i));
            }
            foreach (var (_, track, i) in pairs.OrderBy(p => p.Cost))
            {
                if (!unmatchedTracks.Contains(track) || !unmatchedObs.Contains(i))
                    continue;
                Match(track, i);
            }
        }

        void Match(Track track, int i)
        {
            track.Filter.Update(local[i], observations[i].SigmaMeters);
            track.Filter.CapSpeed(MaxSpeedMps);
            track.Observe(observations[i], frameUtc, frameSeq);
            if (track.State == TrackState.Coasting || (track.State == TrackState.Tentative && track.Hits >= ConfirmHits))
                track.State = TrackState.Confirmed;
            unmatchedTracks.Remove(track);
            unmatchedObs.Remove(i);
            touched.Add(track);
        }

        // The locked target first: its best acceptable sighting, inside the wider gate.
        foreach (var track in unmatchedTracks.Where(t => t.IsLocked).ToList())
        {
            var reach = ReachMarginMeters + MaxSpeedMps * Math.Max((frameUtc - track.LastSeenUtc).TotalSeconds, 0);
            var best = unmatchedObs
                .Select(i => (i, d2: track.Filter.MahalanobisSquared(local[i], observations[i].SigmaMeters)))
                .Where(x => x.d2 <= Kalman2D.Gate999 && ClassesCompatible(track.Class, observations[x.i].Class)
                            && (local[x.i] - track.PositionLocal).Length <= reach && track.Accepts!(observations[x.i]))
                .OrderBy(x => x.d2)
                .Select(x => (int?)x.i)
                .FirstOrDefault();
            if (best is not { } i)
                continue;
            Match(track, i);
        }

        Associate(unmatchedObs.Where(i => observations[i].Score >= HighScore).ToList());
        Associate(unmatchedObs.Where(i => observations[i].Score < HighScore).ToList());

        foreach (var i in unmatchedObs.Where(i => observations[i].Score >= HighScore))
        {
            var track = new Track(_nextId++, new Kalman2D(local[i], observations[i].SigmaMeters), observations[i], frameUtc, frameSeq);
            _tracks.Add(track);
            touched.Add(track);
        }

        // A track that could be the locked target and is within its reach, seen this frame, while
        // the target wasn't: the target's own sightings under another id - fold it back in. One
        // started since the target was last seen at once; an older one only once the target has
        // been missed for MissedBeforeAbsorbSeconds (so a second red car next to it isn't taken
        // the first frame the target is hidden).
        foreach (var target in unmatchedTracks.Where(t => t.IsLocked && t.State is TrackState.Confirmed or TrackState.Coasting).ToList())
        {
            var duplicate = _tracks
                .Where(t => t != target && !t.IsLocked && t.State != TrackState.Lost && t.IsTarget != false
                            && (t.FirstSeenUtc > target.LastSeenUtc || (frameUtc - target.LastSeenUtc).TotalSeconds >= MissedBeforeAbsorbSeconds)
                            && t.LastFrameSeq == frameSeq
                            && ClassesCompatible(target.Class, t.Class) && target.Accepts!(t.Last)
                            && (t.PositionLocal - target.PositionLocal).Length <=
                               ReachMarginMeters + MaxSpeedMps * Math.Max((t.LastSeenUtc - target.LastSeenUtc).TotalSeconds, 0))
                .OrderBy(t => (t.PositionLocal - target.PositionLocal).Length)
                .FirstOrDefault();
            if (duplicate is null)
                continue;
            Absorb(target, duplicate);
            unmatchedTracks.Remove(target);
            touched.Remove(duplicate);
            touched.Add(target);
        }

        foreach (var track in unmatchedTracks)
        {
            if (!inView(PositionOf(track)))
                continue;
            track.MissedInViewSeconds += track.LastFrameDtSeconds;
            if (track.State == TrackState.Tentative && (frameUtc - track.LastSeenUtc).TotalSeconds > TentativeSeconds)
                track.State = TrackState.Lost;
            else if (track.State == TrackState.Confirmed)
                track.State = TrackState.Coasting;
            else if (track.State == TrackState.Coasting && track.MissedInViewSeconds >= CoastSeconds)
                track.State = TrackState.Lost;
        }

        _tracks.RemoveAll(t => (t.State == TrackState.Lost && t.IsTarget != true && (frameUtc - t.LastSeenUtc).TotalSeconds > 2)
                               || (frameUtc - t.LastSeenUtc).TotalSeconds > MemorySeconds);
        return touched;
    }

    /// <summary>Brings a lost track back (reacquisition confirmed by the executive), onto an
    /// observation's track: the old id carries on, the stand-in track is dropped.</summary>
    public void Merge(Track keep, Track into)
    {
        keep.Filter.Reset(into.PositionLocal, into.Last.SigmaMeters, into.VelocityLocal);
        keep.Observe(into.Last, into.LastSeenUtc, into.LastFrameSeq);
        keep.LastUpdateUtc = into.LastUpdateUtc;
        keep.State = TrackState.Confirmed;
        _tracks.Remove(into);
    }

    /// <summary>Folds a young duplicate of the locked target into it: the target moves to where the
    /// duplicate is and keeps its own velocity unless the duplicate has a confirmed one.</summary>
    private void Absorb(Track target, Track duplicate)
    {
        var velocity = duplicate.Hits >= ConfirmHits ? duplicate.VelocityLocal : target.VelocityLocal;
        target.Filter.Reset(duplicate.PositionLocal, duplicate.Last.SigmaMeters, velocity);
        target.Observe(duplicate.Last, duplicate.LastSeenUtc, duplicate.LastFrameSeq);
        target.LastUpdateUtc = duplicate.LastUpdateUtc;
        target.State = TrackState.Confirmed;
        _tracks.Remove(duplicate);
    }

    /// <summary>Car-like classes can be confused frame to frame (car/SUV/van/pickup from above);
    /// a person never becomes a car.</summary>
    public static bool ClassesCompatible(string a, string b) =>
        a == b || (ObjectClasses.IsVehicle(a) && ObjectClasses.IsVehicle(b));
}

/// <summary>Constant-velocity Kalman filter over (east, north, v_east, v_north), in metres.</summary>
public sealed class Kalman2D
{
    /// <summary>Chi-square, 2 dof, 99%.</summary>
    public const double Gate99 = 9.21;

    /// <summary>Chi-square, 2 dof, 99.9%: the locked target's gate.</summary>
    public const double Gate999 = 13.82;

    public double[] X { get; } = new double[4];
    private readonly double[,] _p = new double[4, 4];

    public Kalman2D(Vec2 position, double sigma) => Reset(position, sigma, new Vec2(0, 0));

    public void Reset(Vec2 position, double sigma, Vec2 velocity)
    {
        X[0] = position.X;
        X[1] = position.Y;
        X[2] = velocity.X;
        X[3] = velocity.Y;
        Array.Clear(_p);
        _p[0, 0] = _p[1, 1] = sigma * sigma;
        _p[2, 2] = _p[3, 3] = 15 * 15; // unknown speed: up to ~50 km/h either way is plausible
    }

    public void Predict(double dt, double accelSigma)
    {
        X[0] += X[2] * dt;
        X[1] += X[3] * dt;
        // P = F P F' + Q, F = [I dt·I; 0 I]
        var f = new double[4, 4] { { 1, 0, dt, 0 }, { 0, 1, 0, dt }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };
        var fp = new double[4, 4];
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        for (var k = 0; k < 4; k++)
            fp[r, c] += f[r, k] * _p[k, c];
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var sum = 0.0;
            for (var k = 0; k < 4; k++)
                sum += fp[r, k] * f[c, k];
            _p[r, c] = sum;
        }
        var q = accelSigma * accelSigma;
        for (var axis = 0; axis < 2; axis++)
        {
            int p = axis, v = axis + 2;
            _p[p, p] += q * Math.Pow(dt, 4) / 4;
            _p[p, v] += q * Math.Pow(dt, 3) / 2;
            _p[v, p] += q * Math.Pow(dt, 3) / 2;
            _p[v, v] += q * dt * dt;
        }
    }

    public void CapSpeed(double maxMps)
    {
        var speed = Math.Sqrt(X[2] * X[2] + X[3] * X[3]);
        if (speed <= maxMps)
            return;
        X[2] *= maxMps / speed;
        X[3] *= maxMps / speed;
    }

    public double MahalanobisSquared(Vec2 z, double sigma)
    {
        var (s00, s01, s11) = (_p[0, 0] + sigma * sigma, _p[0, 1], _p[1, 1] + sigma * sigma);
        var det = s00 * s11 - s01 * s01;
        var (dx, dy) = (z.X - X[0], z.Y - X[1]);
        return (s11 * dx * dx - 2 * s01 * dx * dy + s00 * dy * dy) / det;
    }

    public void Update(Vec2 z, double sigma)
    {
        var r = sigma * sigma;
        var (s00, s01, s11) = (_p[0, 0] + r, _p[0, 1], _p[1, 1] + r);
        var det = s00 * s11 - s01 * s01;
        var (i00, i01, i11) = (s11 / det, -s01 / det, s00 / det);
        // K = P H' S^-1 (4x2), H picks the position rows.
        var k = new double[4, 2];
        for (var row = 0; row < 4; row++)
        {
            k[row, 0] = _p[row, 0] * i00 + _p[row, 1] * i01;
            k[row, 1] = _p[row, 0] * i01 + _p[row, 1] * i11;
        }
        var (dx, dy) = (z.X - X[0], z.Y - X[1]);
        for (var row = 0; row < 4; row++)
            X[row] += k[row, 0] * dx + k[row, 1] * dy;
        // P = (I - K H) P
        var p = (double[,])_p.Clone();
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < 4; col++)
            _p[row, col] = p[row, col] - k[row, 0] * p[0, col] - k[row, 1] * p[1, col];
    }
}

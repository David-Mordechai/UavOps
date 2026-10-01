using UavOps.Agent.Mission;

namespace UavOps.Onboard.Detector;

/// <summary>
/// Survey frames overlap, so the same object is usually seen in two or three of them. The tracker
/// turns repeated sightings into one object: a hit near a known one (same mission and prompt) is
/// that object again. Only a new object is reported, plus - for one that drives - a position
/// update once it has moved <see cref="MoveReportMeters"/> since it was last reported.
///
/// "Near" allows for driving: within <see cref="RadiusMeters"/> plus how far a vehicle could have
/// gone since the track was last seen (<see cref="MaxSpeedMps"/>), for up to
/// <see cref="MemorySeconds"/>. After that a sighting is only the same object within the radius:
/// two red cars far apart, seen a minute apart, are two cars, not one that drove.
/// </summary>
public sealed class DetectionTracker(double radiusMeters, double maxSpeedMps = 0, double memorySeconds = 0, double moveReportMeters = 50)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Track>> _tracks = new(StringComparer.Ordinal);
    private int _nextId;

    public double RadiusMeters => radiusMeters;
    public double MaxSpeedMps => maxSpeedMps;
    public double MemorySeconds => memorySeconds;
    public double MoveReportMeters => moveReportMeters;

    public sealed record Track(string Id, GeoPoint Position, double Confidence, int Hits, DateTime LastSeenUtc, GeoPoint ReportedPosition);

    /// <summary>What a sighting means: a new object, or a known one that has moved enough to report again.</summary>
    public sealed record Sighting(Track Track, bool IsNew);

    /// <summary>A new track if this is an object not seen before in this search, else null.</summary>
    public Track? Observe(string missionId, string prompt, GeoPoint position, double confidence) =>
        See(missionId, prompt, position, confidence, DateTime.UtcNow) is { IsNew: true } s ? s.Track : null;

    /// <summary>
    /// Records a sighting at <paramref name="seenAtUtc"/> (when the frame was taken). Returns a new
    /// object, or a known one that has moved far enough to report its new position, or null (seen
    /// again, nothing to report).
    /// </summary>
    public Sighting? See(string missionId, string prompt, GeoPoint position, double confidence, DateTime seenAtUtc)
    {
        var key = missionId + "\n" + prompt.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_tracks.TryGetValue(key, out var tracks))
                _tracks[key] = tracks = [];

            var best = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < tracks.Count; i++)
            {
                var distance = GeoProjection.DistanceMeters(tracks[i].Position, position);
                var elapsed = Math.Abs((seenAtUtc - tracks[i].LastSeenUtc).TotalSeconds);
                var reach = radiusMeters + (elapsed <= memorySeconds ? maxSpeedMps * elapsed : 0);
                if (distance <= reach && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }

            if (best < 0)
            {
                var track = new Track($"trk-{++_nextId}", position, confidence, 1, seenAtUtc, position);
                tracks.Add(track);
                return new Sighting(track, IsNew: true);
            }

            var known = tracks[best];
            var moved = GeoProjection.DistanceMeters(known.ReportedPosition, position) > moveReportMeters;
            tracks[best] = known with
            {
                Position = position,
                Confidence = Math.Max(known.Confidence, confidence),
                Hits = known.Hits + 1,
                LastSeenUtc = seenAtUtc > known.LastSeenUtc ? seenAtUtc : known.LastSeenUtc,
                ReportedPosition = moved ? position : known.ReportedPosition
            };
            return moved ? new Sighting(tracks[best], IsNew: false) : null;
        }
    }
}

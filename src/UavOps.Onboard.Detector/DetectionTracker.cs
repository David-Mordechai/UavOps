using UavOps.Agent.Mission;

namespace UavOps.Onboard.Detector;

/// <summary>
/// Survey frames overlap, so the same object is usually seen in two or three of them. The tracker
/// turns repeated sightings into one object: a hit within <see cref="DetectorOptions.TrackRadiusMeters"/>
/// of a known one (same mission and prompt) is that object again. Only a new object is reported.
/// </summary>
public sealed class DetectionTracker(double radiusMeters)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Track>> _tracks = new(StringComparer.Ordinal);
    private int _nextId;

    public sealed record Track(string Id, GeoPoint Position, double Confidence, int Hits);

    /// <summary>A new track if this is an object not seen before in this search, else null.</summary>
    public Track? Observe(string missionId, string prompt, GeoPoint position, double confidence)
    {
        var key = missionId + "\n" + prompt.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_tracks.TryGetValue(key, out var tracks))
                _tracks[key] = tracks = [];
            for (var i = 0; i < tracks.Count; i++)
            {
                if (GeoProjection.DistanceMeters(tracks[i].Position, position) <= radiusMeters)
                {
                    tracks[i] = tracks[i] with { Confidence = Math.Max(tracks[i].Confidence, confidence), Hits = tracks[i].Hits + 1 };
                    return null;
                }
            }
            var track = new Track($"trk-{++_nextId}", position, confidence, 1);
            tracks.Add(track);
            return track;
        }
    }
}

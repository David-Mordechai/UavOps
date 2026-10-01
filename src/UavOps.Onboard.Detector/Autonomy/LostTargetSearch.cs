using UavOps.Agent.Mission;

namespace UavOps.Onboard.Detector.Autonomy;

/// <summary>
/// Where to look for a target that was lost: the payload steps through look points covering a
/// disc around where the target should be by now, instead of staring at where it was last seen
/// (an operator watching it do that called it "not looking for it" - right).
///
/// The centre is dead-reckoned from the last seen position and velocity, for no longer than
/// <c>maxPredictSeconds</c> (a car keeps its road for a while, but past that the guess is worse
/// than the uncertainty). The radius grows with how far the target could have gone since it was
/// last seen (at least <c>minAssumedSpeedMps</c>: a parked car can drive off). Look points are the
/// centre and then rings, each ring's points spaced by 80% of the frame's ground width so
/// neighbouring looks overlap; the pattern is rebuilt as the disc grows, starting at the centre.
/// </summary>
public sealed class LostTargetSearch(GeoPoint lastSeen, Vec2 velocity, DateTime lastSeenUtc,
    double baseRadiusMeters, double minAssumedSpeedMps, double maxPredictSeconds, double lookWidthMeters)
{
    private readonly GeoProjection _projection = new(lastSeen);
    private List<Vec2> _pattern = [];
    private int _next;

    public GeoPoint Center(DateTime atUtc)
    {
        var dt = Math.Clamp((atUtc - lastSeenUtc).TotalSeconds, 0, maxPredictSeconds);
        return _projection.ToGeo(velocity * dt);
    }

    public double RadiusMeters(DateTime atUtc)
    {
        var elapsed = Math.Max((atUtc - lastSeenUtc).TotalSeconds, 0);
        return baseRadiusMeters + Math.Max(velocity.Length, minAssumedSpeedMps) * elapsed;
    }

    /// <summary>The next point to aim the payload at; after the last one the pattern is rebuilt for
    /// the disc as it is now (it only grows), from the centre again.</summary>
    public GeoPoint NextLook(DateTime atUtc)
    {
        if (_next >= _pattern.Count)
        {
            _pattern = Pattern(RadiusMeters(atUtc), lookWidthMeters);
            _next = 0;
        }
        var center = new GeoProjection(Center(atUtc));
        return center.ToGeo(_pattern[_next++]);
    }

    /// <summary>Look offsets (metres east, north) covering a disc: the centre, then rings.</summary>
    public static List<Vec2> Pattern(double radiusMeters, double lookWidthMeters)
    {
        var step = Math.Max(lookWidthMeters * 0.8, 1);
        var points = new List<Vec2> { new(0, 0) };
        for (var ring = 1; ring * step - step / 2 < radiusMeters; ring++)
        {
            var r = Math.Min(ring * step, radiusMeters);
            var count = Math.Max(4, (int)Math.Ceiling(2 * Math.PI * r / step));
            // Each ring starts at a different angle, so rings don't line their points up.
            var offset = ring * 0.5;
            for (var i = 0; i < count; i++)
            {
                var a = offset + 2 * Math.PI * i / count;
                points.Add(new Vec2(Math.Sin(a) * r, Math.Cos(a) * r));
            }
        }
        return points;
    }
}

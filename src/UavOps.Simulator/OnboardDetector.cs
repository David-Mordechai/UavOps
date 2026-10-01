using UavOps.Agent.Mission;
using UavOps.FleetClient;
using UavOps.Onboard.Contracts;

namespace UavOps.Simulator;

/// <summary>
/// The onboard agent's eyes: given where a UAV is and what it's looking for, what does it see?
/// <see cref="OnboardDetectorClient"/> hands the real question to the onboard detection service
/// (UavOps.Onboard.Detector, a vision model over the camera frames); <see cref="SimulatedDetector"/>
/// answers it without a model. Either way the contract with the rest of the system is the same
/// <see cref="SearchTargetRequest"/> in and <see cref="DetectionReport"/> out. Called on the sim
/// tick under the fleet lock, so it must not block.
/// </summary>
public interface IOnboardDetector
{
    IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc);

    /// <summary>Whether everything the camera took for this UAV's search has been looked at, so
    /// the search can be reported complete once its route is flown.</summary>
    bool HasFinished(SimUav uav);

    /// <summary>The next report from the onboard computer about a target it tracks for this UAV
    /// (find and track), or null. Called on the tick under the fleet lock.</summary>
    TargetTrackReport? TakeTrack(SimUav uav) => null;
}

/// <summary>
/// The no-model fallback (<c>Simulator:Detector = Simulated</c>): sees a scenario object when it's
/// inside the camera frame (<see cref="CameraModel"/>) and every word of the search prompt is one
/// of its tags. Reports each object once per mission, with a few meters of position noise - and
/// again, under the same track id, when a driving one is seen more than <see cref="MovedMeters"/>
/// from where it was last reported.
/// </summary>
public sealed class SimulatedDetector(ScenarioStore scenario, SimOptions options, World.GroundWorld? world = null) : IOnboardDetector
{
    public const double MovedMeters = 50;

    private readonly Dictionary<(string MissionId, string ObjectId), GeoPoint> _reported = [];
    private readonly Random _random = new();

    public IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc)
    {
        if (!uav.IsLooking || uav.SearchPrompt is null || uav.MissionId is null)
            yield break;

        var wanted = TextWords.Of(uav.SearchPrompt).ToList();
        if (wanted.Count == 0)
            yield break;

        var camera = new CameraModel(uav.Position.Lat, uav.Position.Lng, Math.Max(uav.AltitudeFt, 1), uav.HeadingDeg,
            uav.PayloadHfovDeg, options.CameraWidth, options.CameraHeight);
        var projection = new GeoProjection(uav.Position);
        // Where things are now: a driving target is seen where it has got to.
        foreach (var obj in world?.ObjectsNow() ?? scenario.All())
        {
            if (!wanted.All(obj.Tags.Contains))
                continue;
            if (!camera.Contains(new GeoPoint(obj.Lat, obj.Lng)))
                continue;
            var at = new GeoPoint(obj.Lat, obj.Lng);
            if (_reported.TryGetValue((uav.MissionId, obj.Id), out var last) && GeoProjection.DistanceMeters(last, at) <= MovedMeters)
                continue;
            _reported[(uav.MissionId, obj.Id)] = at;

            var seen = projection.ToGeo(projection.ToLocal(new GeoPoint(obj.Lat, obj.Lng)) + new Vec2(Jitter(), Jitter()));
            yield return new DetectionReport
            {
                TailNumber = uav.TailNumber,
                MissionId = uav.MissionId,
                ZoneName = uav.ZoneName ?? "",
                Prompt = uav.SearchPrompt,
                Label = obj.Label,
                Confidence = Math.Round(0.8 + _random.NextDouble() * 0.15, 2),
                Lat = Math.Round(seen.Lat, 6),
                Lng = Math.Round(seen.Lng, 6),
                DetectedAtUtc = nowUtc,
                TrackId = obj.Id
            };
        }
    }

    /// <summary>Looks as it flies: nothing is ever left to look at.</summary>
    public bool HasFinished(SimUav uav) => true;

    private double Jitter() => (_random.NextDouble() - 0.5) * 10;
}

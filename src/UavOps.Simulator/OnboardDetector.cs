using UavOps.Agent.Mission;
using UavOps.FleetClient;

namespace UavOps.Simulator;

/// <summary>
/// The onboard agent's eyes: given where a UAV is and what it's looking for, what does it see?
/// This is the seam the real onboard vision service (a Jetson running an open-vocabulary
/// detector) replaces; its contract with the rest of the system is the same
/// <see cref="SearchTargetRequest"/> in and <see cref="DetectionReport"/> out.
/// </summary>
public interface IOnboardDetector
{
    IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc);
}

/// <summary>
/// Sees a scenario object when it's inside the camera footprint (a circle under the UAV,
/// radius altitude·tan(HFOV/2)) and every word of the search prompt is one of its tags.
/// Reports each object once per mission, with a few meters of position noise.
/// </summary>
public sealed class SimulatedDetector(ScenarioStore scenario, SimOptions options) : IOnboardDetector
{
    private readonly HashSet<(string MissionId, string ObjectId)> _reported = [];
    private readonly Random _random = new();

    public double FootprintRadiusMeters(double altitudeFt) =>
        Math.Max(altitudeFt, 0) * SearchPlanParameters.MetersPerFoot * Math.Tan(options.CameraHorizontalFovDeg * Math.PI / 360);

    public IEnumerable<DetectionReport> Look(SimUav uav, DateTime nowUtc)
    {
        if (!uav.IsLooking || uav.SearchPrompt is null || uav.MissionId is null)
            yield break;

        var wanted = TextWords.Of(uav.SearchPrompt).ToList();
        if (wanted.Count == 0)
            yield break;

        var radius = FootprintRadiusMeters(uav.AltitudeFt);
        var projection = new GeoProjection(uav.Position);
        foreach (var obj in scenario.All())
        {
            if (!wanted.All(obj.Tags.Contains))
                continue;
            if (projection.ToLocal(new GeoPoint(obj.Lat, obj.Lng)).Length > radius)
                continue;
            if (!_reported.Add((uav.MissionId, obj.Id)))
                continue;

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

    private double Jitter() => (_random.NextDouble() - 0.5) * 10;
}

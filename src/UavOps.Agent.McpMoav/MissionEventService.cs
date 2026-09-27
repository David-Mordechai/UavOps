using System.Globalization;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;
using UavOps.Agent.Mission;

namespace UavOps.Agent.McpMoav;

/// <summary>
/// What the fleet app reports unprompted about a search mission (a detection, the mission
/// ending), turned into operator messages. The fleet app sends these to the host
/// (<c>ChatHub.ReportDetection</c>/<c>ReportMissionEvent</c>), which forwards them here unchanged
/// (<see cref="HostHubContract.FleetEvents"/>); everything they mean is decided here.
///
/// The messages are fixed text, not phrased by the model: they carry coordinates, and a small
/// model can garble digits. Each one also goes into BrainAgent's history (the history note), and
/// a detection into <see cref="DetectionPointRegistry"/>, so the operator can follow up with
/// "send 998 to the white van". After a detection the UAV keeps searching; what happens next is
/// the operator's call.
/// </summary>
public sealed class MissionEventService(
    IOperatorNotifier notifier,
    DetectionPointRegistry points,
    MissionOptions options,
    ILogger<MissionEventService> logger)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<DetectionReport>> _detectionsByMission = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _promptByMission = new(StringComparer.Ordinal);
    private int _detectionCount;

    /// <summary>Called as a search target goes out to a UAV, so a mission that ends with nothing
    /// found can still say what it was looking for.</summary>
    public void RememberSearchTarget(SearchTargetRequest request)
    {
        lock (_lock)
        {
            _promptByMission[request.MissionId] = request.Prompt;
        }
    }

    /// <summary>Returns false (and tells no one) for a report of an object already reported.</summary>
    public async Task<bool> HandleDetectionAsync(DetectionReport report)
    {
        if (report.Lat is < -90 or > 90 || report.Lng is < -180 or > 180 || string.IsNullOrWhiteSpace(report.Prompt))
        {
            logger.LogWarning("Ignoring malformed detection report from {TailNumber}: {@Report}", report.TailNumber, report);
            return false;
        }

        int number;
        lock (_lock)
        {
            var key = MissionKey(report.MissionId, report.Prompt);
            if (!_detectionsByMission.TryGetValue(key, out var seen))
                _detectionsByMission[key] = seen = [];

            if (seen.Any(s => DistanceMeters(s.Lat, s.Lng, report.Lat, report.Lng) <= options.DetectionDedupeRadiusMeters))
            {
                logger.LogInformation("Duplicate detection of '{Prompt}' by {TailNumber} in mission {MissionId}, not reported again.",
                    report.Prompt, report.TailNumber, report.MissionId);
                return false;
            }

            seen.Add(report);
            _promptByMission.TryAdd(report.MissionId, report.Prompt);
            number = ++_detectionCount;
        }

        var target = report.Prompt.Trim();
        points.Register(report.Lat, report.Lng, target, $"detection {number}", $"detection #{number}");

        // The note gives the "lat,lng" literal rather than the name: both resolve here, but the
        // literal doesn't depend on DetectionPointRegistry, so it's what the model should copy.
        var latLng = DetectionPointRegistry.FormatLatLng(report.Lat, report.Lng);
        var message = DescribeDetection(report);
        logger.LogInformation("Detection {Number}: {Message}", number, message);
        await notifier.PostAsync(message,
            $"UAV {report.TailNumber}'s onboard agent reported a detection (detection {number}, the {target}) while searching " +
            $"{ZoneText(report.ZoneName)}, and the operator was shown the message below. This is only a report: no UAV " +
            $"has been sent there and nothing else has been done about it. To send a UAV there or point a payload at it, " +
            $"call that tool with location '{latLng}' - it only happens if you call the tool.");
        return true;
    }

    public async Task HandleMissionEventAsync(MissionEventReport report)
    {
        List<DetectionReport> detections;
        string prompt;
        lock (_lock)
        {
            detections = _detectionsByMission
                .Where(kv => kv.Key.StartsWith(report.MissionId + "\n", StringComparison.Ordinal))
                .SelectMany(kv => kv.Value)
                .OrderBy(d => d.DetectedAtUtc)
                .ToList();
            prompt = _promptByMission.GetValueOrDefault(report.MissionId) ?? "the search target";
        }

        var zone = ZoneText(report.ZoneName);
        if (string.Equals(report.Kind, MissionEventKinds.Completed, StringComparison.OrdinalIgnoreCase))
        {
            var message = detections.Count == 0
                ? $"{report.TailNumber} finished searching {zone} - no {prompt} found."
                : $"{report.TailNumber} finished searching {zone} - {Plural(detections.Count, "detection")} of {prompt}, " +
                  $"the last at {Coordinates(detections[^1])}.";
            await notifier.PostAsync(message, $"UAV {report.TailNumber} reported that its search mission ended: it flew the whole route.");
        }
        else
        {
            // Aborted means the operator redirected the UAV, so they already know; only the
            // model's history needs to reflect that the search is over.
            await notifier.AddHistoryNoteAsync(
                $"UAV {report.TailNumber} reported that its search of {zone} stopped before the end of its route.",
                $"{report.TailNumber} stopped searching {zone} before finishing.");
        }
    }

    /// <summary>"White van detected at 31.81234, 34.66123 by 997 while searching ZoneA (confidence 87%)."</summary>
    public static string DescribeDetection(DetectionReport report)
    {
        var target = report.Prompt.Trim();
        target = char.ToUpperInvariant(target[0]) + target[1..];
        return string.Create(CultureInfo.InvariantCulture,
            $"{target} detected at {Coordinates(report)} by {report.TailNumber} while searching {ZoneText(report.ZoneName)} " +
            $"(confidence {report.Confidence * 100:F0}%).");
    }

    private static string MissionKey(string missionId, string prompt) => missionId + "\n" + prompt.Trim().ToLowerInvariant();

    private static string ZoneText(string zoneName) => string.IsNullOrWhiteSpace(zoneName) ? "its area" : zoneName;

    private static string Coordinates(DetectionReport report) =>
        string.Create(CultureInfo.InvariantCulture, $"{report.Lat:F5}, {report.Lng:F5}");

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadius = 6_371_008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * earthRadius * Math.Asin(Math.Sqrt(h));
    }
}

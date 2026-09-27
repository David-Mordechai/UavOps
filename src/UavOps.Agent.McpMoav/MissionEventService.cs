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
///
/// Every detection is numbered ("Detection 5"), the name the operator can use for it. A busy search
/// doesn't post one message per object: the first <see cref="MissionOptions.DetectionsReportedIndividually"/>
/// of a mission are posted as they come, the rest are gathered and posted as one summary every
/// <see cref="MissionOptions.DetectionSummaryIntervalSeconds"/> (<see cref="FlushDueSummariesAsync"/>,
/// driven by <see cref="DetectionSummaryFlusher"/>) - a live search once posted 51 red-car messages
/// in a row, which neither the operator nor the voice could keep up with.
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
    private readonly Dictionary<string, PendingSummary> _pending = new(StringComparer.Ordinal);
    private int _detectionCount;

    /// <summary>Detections of one mission waiting for the next summary.</summary>
    private sealed class PendingSummary(DateTime dueUtc)
    {
        public DateTime DueUtc { get; } = dueUtc;
        public List<(int Number, DetectionReport Report)> Detections { get; } = [];
    }

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
    public Task<bool> HandleDetectionAsync(DetectionReport report) => HandleDetectionAsync(report, DateTime.UtcNow);

    public async Task<bool> HandleDetectionAsync(DetectionReport report, DateTime nowUtc)
    {
        if (report.Lat is < -90 or > 90 || report.Lng is < -180 or > 180 || string.IsNullOrWhiteSpace(report.Prompt))
        {
            logger.LogWarning("Ignoring malformed detection report from {TailNumber}: {@Report}", report.TailNumber, report);
            return false;
        }

        int number;
        bool individually;
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

            individually = seen.Count <= options.DetectionsReportedIndividually;
            if (!individually)
            {
                if (!_pending.TryGetValue(key, out var pending))
                    _pending[key] = pending = new PendingSummary(nowUtc.AddSeconds(options.DetectionSummaryIntervalSeconds));
                pending.Detections.Add((number, report));
            }
        }

        var target = report.Prompt.Trim();
        points.Register(report.Lat, report.Lng, target, $"detection {number}", $"detection #{number}");
        if (!individually)
        {
            logger.LogInformation("Detection {Number} of '{Prompt}' by {TailNumber} held for the next summary.", number, target, report.TailNumber);
            return true;
        }

        // The note gives the "lat,lng" literal rather than the name: both resolve here, but the
        // literal doesn't depend on DetectionPointRegistry, so it's what the model should copy.
        var latLng = DetectionPointRegistry.FormatLatLng(report.Lat, report.Lng);
        var message = $"Detection {number}: {DescribeDetection(report)}";
        logger.LogInformation("Detection {Number}: {Message}", number, message);
        var voice = new OperatorVoice($"Detection {number}: {target}, by {report.TailNumber}.", VoiceGroup(report.MissionId));
        await notifier.PostAsync(message,
            $"UAV {report.TailNumber}'s onboard agent reported a detection (detection {number}, the {target}) while searching " +
            $"{ZoneText(report.ZoneName)}, and the operator was shown the message below. This is only a report: no UAV " +
            $"has been sent there and nothing else has been done about it. To send a UAV there or point a payload at it, " +
            $"call that tool with location '{latLng}' - it only happens if you call the tool.",
            voice);
        return true;
    }

    /// <summary>Posts every detection summary that's due (all of them with <paramref name="force"/>).</summary>
    public async Task FlushDueSummariesAsync(DateTime nowUtc, bool force = false)
    {
        List<PendingSummary> due;
        lock (_lock)
        {
            var keys = _pending.Where(kv => force || kv.Value.DueUtc <= nowUtc).Select(kv => kv.Key).ToList();
            due = keys.Select(k => _pending[k]).ToList();
            foreach (var key in keys)
                _pending.Remove(key);
        }
        foreach (var summary in due)
            await PostSummaryAsync(summary.Detections);
    }

    private async Task FlushMissionAsync(string missionId)
    {
        List<PendingSummary> due;
        lock (_lock)
        {
            var keys = _pending.Keys.Where(k => k.StartsWith(missionId + "\n", StringComparison.Ordinal)).ToList();
            due = keys.Select(k => _pending[k]).ToList();
            foreach (var key in keys)
                _pending.Remove(key);
        }
        foreach (var summary in due)
            await PostSummaryAsync(summary.Detections);
    }

    /// <summary>"Detections 4-8: 5 more red cars found by 997 in ZoneA. Most confident: #6 at ..."
    /// The message lists the most confident few; the history note has every one's position.</summary>
    private async Task PostSummaryAsync(List<(int Number, DetectionReport Report)> detections)
    {
        var first = detections[0].Report;
        var target = first.Prompt.Trim();
        var numbers = NumberList(detections.Select(d => d.Number).ToList());
        var listed = detections.OrderByDescending(d => d.Report.Confidence).Take(Math.Max(options.MaxDetectionsListedInSummary, 1)).ToList();
        var rest = detections.Count - listed.Count;

        var shown = string.Join("; ", listed.Select(d =>
            FormattableString.Invariant($"#{d.Number} at {Coordinates(d.Report)} ({d.Report.Confidence * 100:F0}%)")));
        var message =
            $"{(detections.Count == 1 ? "Detection" : "Detections")} {numbers}: {Plural(detections.Count, "more " + target)} found by " +
            $"{first.TailNumber} while searching {ZoneText(first.ZoneName)}. " +
            $"{(detections.Count == 1 ? "At" : "Most confident:")} {shown}{(rest > 0 ? "; and " + rest + " more" : "")}.";
        var all = string.Join("; ", detections.Select(d => FormattableString.Invariant(
            $"detection {d.Number} at location '{DetectionPointRegistry.FormatLatLng(d.Report.Lat, d.Report.Lng)}' ({d.Report.Confidence * 100:F0}%)")));
        var note =
            $"UAV {first.TailNumber}'s onboard agent reported {Plural(detections.Count, "more detection")} of the {target} while searching " +
            $"{ZoneText(first.ZoneName)}, shown to the operator as one summary: {all}. This is only a report: no UAV has been sent " +
            "anywhere and nothing else has been done about it. To send a UAV to one or point a payload at it, call that tool with " +
            "its location - it only happens if you call the tool.";
        logger.LogInformation("Detection summary: {Message}", message);
        var spokenNumbers = numbers.Replace("-", " to ", StringComparison.Ordinal);
        var voice = new OperatorVoice(
            $"{(detections.Count == 1 ? "Detection" : "Detections")} {spokenNumbers}: {Plural(detections.Count, "more " + target)}, by {first.TailNumber}.",
            VoiceGroup(first.MissionId));
        await notifier.PostAsync(message, note, voice);
    }

    /// <summary>"4-8", or "4, 5 and 9" when they aren't consecutive.</summary>
    private static string NumberList(List<int> numbers)
    {
        if (numbers.Count == 1)
            return numbers[0].ToString(CultureInfo.InvariantCulture);
        if (numbers[^1] - numbers[0] == numbers.Count - 1)
            return $"{numbers[0]}-{numbers[^1]}";
        return string.Join(", ", numbers.Take(numbers.Count - 1)) + " and " + numbers[^1];
    }

    public async Task HandleMissionEventAsync(MissionEventReport report)
    {
        // Anything still held for a summary goes out before the mission's end is announced.
        await FlushMissionAsync(report.MissionId);

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
            var spoken = detections.Count == 0
                ? $"{report.TailNumber} finished searching {zone}: no {prompt} found."
                : $"{report.TailNumber} finished searching {zone}: {Plural(detections.Count, "detection")} of {prompt}.";
            await notifier.PostAsync(message, $"UAV {report.TailNumber} reported that its search mission ended: it flew the whole route.",
                new OperatorVoice(spoken));
        }
        else
        {
            // Aborted means the operator redirected the UAV or gave it a new search, so they
            // already know; only the model's history needs to reflect that the search is over.
            await notifier.AddHistoryNoteAsync(
                $"UAV {report.TailNumber} reported that its search of {zone} for {prompt} stopped before it finished " +
                $"(the UAV was redirected or given a new search).",
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

    private static string VoiceGroup(string missionId) => "detections:" + missionId;

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

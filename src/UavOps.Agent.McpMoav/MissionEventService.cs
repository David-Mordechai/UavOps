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
///
/// A team search (<see cref="RememberTeam"/>: one zone split between several UAVs, each flying its
/// own mission) is one search as far as the operator is concerned: an object on the border seen by
/// two members is reported once, the "first few individually, then summaries" count is per team,
/// a member finishing early gets a short message, and the last one to finish gets one summary for
/// the whole team. A member that stops early (redirected) leaves its strip unsearched; that is only
/// reported, never re-planned.
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
    private readonly Dictionary<string, Team> _teamByMission = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tracked> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _passesByMission = new(StringComparer.Ordinal);

    /// <summary>Missions whose UAV is locked on its target and following it (find and track).</summary>
    private readonly HashSet<string> _following = new(StringComparer.Ordinal);
    private int _detectionCount;

    /// <summary>A reported detection the aircraft can report again under its track id, when the
    /// object has moved (a vehicle driving): its number, where it was last, and when the operator was
    /// last told about it moving.</summary>
    private sealed class Tracked(int number, DetectionReport last)
    {
        public int Number { get; } = number;
        public DetectionReport Last { get; set; } = last;
        public DateTime LastMessageUtc { get; set; } = DateTime.MinValue;
    }

    /// <summary>Detections of one search (a mission, or a team's missions) waiting for the next summary.</summary>
    private sealed class PendingSummary(DateTime dueUtc, string searchId)
    {
        public DateTime DueUtc { get; } = dueUtc;
        public string SearchId { get; } = searchId;
        public List<(int Number, DetectionReport Report)> Detections { get; } = [];
    }

    /// <summary>A zone split between UAVs: each member's mission id, and how each ended (null while
    /// still searching).</summary>
    private sealed class Team(string id, IReadOnlyList<(string Tail, string MissionId)> members)
    {
        public string Id { get; } = id;
        public IReadOnlyList<(string Tail, string MissionId)> Members { get; } = members;
        public Dictionary<string, string?> EndByMission { get; } = members.ToDictionary(m => m.MissionId, _ => (string?)null, StringComparer.Ordinal);
    }

    /// <summary>Missions ended on purpose by a mission plan or its one-tracker rule
    /// (<see cref="MarkStoppedOnPurpose"/>): their end was already reported, so it isn't again.</summary>
    private readonly HashSet<string> _stoppedOnPurpose = new(StringComparer.Ordinal);

    /// <summary>A UAV's search is being ended deliberately (a plan step, or the one-tracker rule):
    /// its coming Aborted event isn't announced as "stopped before finishing".</summary>
    public void MarkStoppedOnPurpose(string missionId)
    {
        lock (_lock)
            _stoppedOnPurpose.Add(missionId);
    }

    /// <summary>The mission id a UAV's current search carries, from its last detection or target.</summary>
    public string? MissionOf(string tail)
    {
        lock (_lock)
            return _missionByTail.GetValueOrDefault(tail);
    }

    private readonly Dictionary<string, string> _missionByTail = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called as a team search is prepared, before any member's target goes out.</summary>
    public void RememberTeam(string teamId, IReadOnlyList<(string TailNumber, string MissionId)> members)
    {
        var team = new Team("team:" + teamId, members);
        lock (_lock)
        {
            foreach (var (_, missionId) in members)
                _teamByMission[missionId] = team;
        }
    }

    /// <summary>What detections are grouped by: the team for a team member's mission, else the
    /// mission itself. Call under <see cref="_lock"/>.</summary>
    private string SearchId(string missionId) => _teamByMission.TryGetValue(missionId, out var team) ? team.Id : missionId;

    /// <summary>Called as a search target goes out to a UAV, so a mission that ends with nothing
    /// found can still say what it was looking for.</summary>
    public void RememberSearchTarget(SearchTargetRequest request, string? tailNumber = null)
    {
        lock (_lock)
        {
            _promptByMission[request.MissionId] = request.Prompt;
            if (tailNumber is not null)
                _missionByTail[tailNumber] = request.MissionId;
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
        string searchId;
        Tracked? moved = null;
        DetectionReport? previous = null;
        lock (_lock)
        {
            searchId = SearchId(report.MissionId);
            // Seen again under a track already reported: it moved - an update, not a new detection.
            if (report.TrackId is { } trackId && _tracks.TryGetValue(searchId + "\n" + trackId, out var tracked))
            {
                // Barely moved: the same sighting again (another frame, another team member).
                if (DistanceMeters(tracked.Last.Lat, tracked.Last.Lng, report.Lat, report.Lng) <= options.MovedReportMeters)
                    return false;
                previous = tracked.Last;
                tracked.Last = report;
                moved = tracked;
                if (_detectionsByMission.TryGetValue(MissionKey(searchId, report.Prompt), out var list))
                    for (var i = 0; i < list.Count; i++)
                        if (list[i].TrackId == trackId)
                            list[i] = report;
            }
        }
        if (moved is not null)
            return await HandleMovedAsync(moved, previous!, report, nowUtc);

        lock (_lock)
        {
            var key = MissionKey(searchId, report.Prompt);
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
            if (report.TrackId is { } newTrack)
                _tracks[searchId + "\n" + newTrack] = new Tracked(number, report);

            individually = seen.Count <= options.DetectionsReportedIndividually;
            if (!individually)
            {
                if (!_pending.TryGetValue(key, out var pending))
                    _pending[key] = pending = new PendingSummary(nowUtc.AddSeconds(options.DetectionSummaryIntervalSeconds), searchId);
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
        var voice = new OperatorVoice($"Detection {number}: {target}, by {report.TailNumber}.", VoiceGroup(searchId));
        await notifier.PostAsync(message,
            $"UAV {report.TailNumber}'s onboard agent reported a detection (detection {number}, the {target}) while searching " +
            $"{ZoneText(report.ZoneName)} at {Time(report.DetectedAtUtc)}, and the operator was shown the message below. This is only a report: no UAV " +
            $"has been sent there and nothing else has been done about it. To send a UAV there or point a payload at it, " +
            $"call that tool with location '{latLng}' - it only happens if you call the tool.",
            voice);
        return true;
    }

    /// <summary>
    /// A detection seen again somewhere else: a vehicle that drives. Its name ("the red car",
    /// "detection 3") now points at where it is, so "send 998 to the red car" goes there; the model's
    /// history always gets the new position, the operator a short message at most every
    /// <see cref="MissionOptions.MovedMessageIntervalSeconds"/> per detection (a car followed along a
    /// road would otherwise post one per frame). Speed and direction come from the last two sightings.
    /// </summary>
    private async Task<bool> HandleMovedAsync(Tracked tracked, DetectionReport previous, DetectionReport report, DateTime nowUtc)
    {
        var target = report.Prompt.Trim();
        var number = tracked.Number;
        points.Register(report.Lat, report.Lng, target, $"detection {number}", $"detection #{number}");

        var meters = DistanceMeters(previous.Lat, previous.Lng, report.Lat, report.Lng);
        var seconds = (report.DetectedAtUtc - previous.DetectedAtUtc).TotalSeconds;
        var motion = seconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $", moving ~{meters / seconds * 3.6:F0} km/h {Compass(previous, report)}")
            : "";
        var latLng = DetectionPointRegistry.FormatLatLng(report.Lat, report.Lng);
        var note =
            $"Detection {number} (the {target}) was seen again by UAV {report.TailNumber} at {Time(report.DetectedAtUtc)}, " +
            string.Create(CultureInfo.InvariantCulture, $"{meters:F0} m from where it was last reported{motion}: it is moving. Its latest known location is '{latLng}'; ") +
            "'the " + target + "' and 'detection " + number + "' now mean that location. This is only a report: nothing has been done about it.";
        var message = $"Detection {number} ({target}) seen again by {report.TailNumber} at {Coordinates(report)} ({Time(report.DetectedAtUtc)}){motion}.";

        bool tell;
        lock (_lock)
        {
            // A target the UAV is locked on and following: the operator already knows it's being
            // followed ("997 locked on ..."), and only hears when it's lost or found again - its
            // position updates only go into the model's history (an operator called the message
            // every 30 s while following noise).
            tell = !_following.Contains(report.MissionId)
                   && (nowUtc - tracked.LastMessageUtc).TotalSeconds >= options.MovedMessageIntervalSeconds;
            if (tell)
                tracked.LastMessageUtc = nowUtc;
        }
        logger.LogInformation("Detection {Number} moved: {Message}", number, message);
        if (tell)
            await notifier.PostAsync(message, note, new OperatorVoice($"Detection {number}, {target}, is moving{motion}.", VoiceGroup(SearchIdOf(report.MissionId))));
        else
            await notifier.AddHistoryNoteAsync(note, message);
        return true;
    }

    private string SearchIdOf(string missionId)
    {
        lock (_lock) return SearchId(missionId);
    }

    /// <summary>"NE": the direction from one sighting to the next.</summary>
    private static string Compass(DetectionReport from, DetectionReport to)
    {
        var dLat = to.Lat - from.Lat;
        var dLng = (to.Lng - from.Lng) * Math.Cos(from.Lat * Math.PI / 180);
        var bearing = (Math.Atan2(dLng, dLat) * 180 / Math.PI + 360) % 360;
        return new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(bearing / 45) % 8];
    }

    /// <summary>"12:53:05 UTC".</summary>
    private static string Time(DateTime utc) => utc.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

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
            await PostSummaryAsync(summary);
    }

    private async Task FlushSearchAsync(string searchId)
    {
        List<PendingSummary> due;
        lock (_lock)
        {
            var keys = _pending.Keys.Where(k => k.StartsWith(searchId + "\n", StringComparison.Ordinal)).ToList();
            due = keys.Select(k => _pending[k]).ToList();
            foreach (var key in keys)
                _pending.Remove(key);
        }
        foreach (var summary in due)
            await PostSummaryAsync(summary);
    }

    /// <summary>"Detections 4-8: 5 more red cars found by 997 in ZoneA. Most confident: #6 at ..."
    /// The message lists the most confident few; the history note has every one's position.</summary>
    private async Task PostSummaryAsync(PendingSummary summary)
    {
        var detections = summary.Detections;
        var first = detections[0].Report;
        var finders = string.Join(" and ", detections.Select(d => d.Report.TailNumber).Distinct(StringComparer.OrdinalIgnoreCase));
        var target = first.Prompt.Trim();
        var numbers = NumberList(detections.Select(d => d.Number).ToList());
        var listed = detections.OrderByDescending(d => d.Report.Confidence).Take(Math.Max(options.MaxDetectionsListedInSummary, 1)).ToList();
        var rest = detections.Count - listed.Count;

        var shown = string.Join("; ", listed.Select(d =>
            FormattableString.Invariant($"#{d.Number} at {Coordinates(d.Report)} ({d.Report.Confidence * 100:F0}%)")));
        var message =
            $"{(detections.Count == 1 ? "Detection" : "Detections")} {numbers}: {Plural(detections.Count, "more " + target)} found by " +
            $"{finders} while searching {ZoneText(first.ZoneName)}. " +
            $"{(detections.Count == 1 ? "At" : "Most confident:")} {shown}{(rest > 0 ? "; and " + rest + " more" : "")}.";
        var all = string.Join("; ", detections.Select(d => FormattableString.Invariant(
            $"detection {d.Number} at location '{DetectionPointRegistry.FormatLatLng(d.Report.Lat, d.Report.Lng)}' ({d.Report.Confidence * 100:F0}%)")));
        var note =
            $"{OnboardAgents(finders)} reported {Plural(detections.Count, "more detection")} of the {target} while searching " +
            $"{ZoneText(first.ZoneName)}, shown to the operator as one summary: {all}. This is only a report: no UAV has been sent " +
            "anywhere and nothing else has been done about it. To send a UAV to one or point a payload at it, call that tool with " +
            "its location - it only happens if you call the tool.";
        logger.LogInformation("Detection summary: {Message}", message);
        var spokenNumbers = numbers.Replace("-", " to ", StringComparison.Ordinal);
        var voice = new OperatorVoice(
            $"{(detections.Count == 1 ? "Detection" : "Detections")} {spokenNumbers}: {Plural(detections.Count, "more " + target)}, by {finders}.",
            VoiceGroup(summary.SearchId));
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
        // Find and track: not an end - the UAV locked on its target, lost it, or found it again.
        if (report.Kind is MissionEventKinds.Tracking or MissionEventKinds.TargetLost or MissionEventKinds.TargetRegained or MissionEventKinds.SearchResumed)
        {
            await HandleTrackingEventAsync(report);
            return;
        }
        // A repeating search finished one pass and goes again: not an end either.
        if (report.Kind == MissionEventKinds.PassCompleted)
        {
            await HandlePassCompletedAsync(report);
            return;
        }

        Team? team;
        lock (_lock)
        {
            team = _teamByMission.GetValueOrDefault(report.MissionId);
            if (team is not null)
                team.EndByMission[report.MissionId] = report.Kind;
        }
        bool onPurpose;
        lock (_lock)
            onPurpose = _stoppedOnPurpose.Contains(report.MissionId);
        if (onPurpose && !IsCompleted(report.Kind))
            return; // ended by a plan step or the one-tracker rule: already reported
        if (team is not null)
        {
            await HandleTeamMemberEndAsync(team, report);
            return;
        }

        // Anything still held for a summary goes out before the mission's end is announced.
        await FlushSearchAsync(report.MissionId);

        var (detections, prompt) = DetectionsAndPrompt(report.MissionId, report.MissionId);
        var zone = ZoneText(report.ZoneName);
        if (IsCompleted(report.Kind))
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

    /// <summary>
    /// A find-and-track mission's news: the onboard agent locked on the target it found (the UAV now
    /// circles it, and its position keeps coming as detection updates), lost it, or found it again.
    /// Named by the detection number the operator already knows it by.
    /// </summary>
    private async Task HandleTrackingEventAsync(MissionEventReport report)
    {
        Tracked? tracked;
        string? prompt;
        lock (_lock)
        {
            var searchId = SearchId(report.MissionId);
            tracked = _tracks.Where(kv => kv.Key.StartsWith(searchId + "\n", StringComparison.Ordinal) && kv.Value.Last.MissionId == report.MissionId)
                .Select(kv => kv.Value)
                .MaxBy(t => t.Last.DetectedAtUtc);
            prompt = _promptByMission.GetValueOrDefault(report.MissionId);
        }
        lock (_lock)
        {
            if (report.Kind is MissionEventKinds.Tracking or MissionEventKinds.TargetRegained)
                _following.Add(report.MissionId);
            else
                _following.Remove(report.MissionId);
        }
        var target = tracked?.Last.Prompt.Trim() ?? prompt ?? "the target";
        var name = tracked is null ? $"the {target}" : $"detection {tracked.Number} ({target})";
        var where = tracked is null ? "" : $" near {Coordinates(tracked.Last)}";
        var tail = report.TailNumber;
        switch (report.Kind)
        {
            case MissionEventKinds.Tracking:
                await notifier.PostAsync(
                    $"{tail} locked on {name} and is following it.",
                    $"UAV {tail}'s onboard agent locked its payload on {name} and the UAV now circles it, following it as it moves; " +
                    "its latest position keeps coming as detection updates. The search is over; the UAV keeps following until the " +
                    "operator sends it elsewhere.",
                    new OperatorVoice($"{tail} locked on the {target} and is following it."));
                // "Stop tracking", "zoom in more" now mean this UAV, without "Which UAV?".
                await notifier.SetOperatorUavAsync(tail);
                break;
            case MissionEventKinds.TargetLost:
                await notifier.PostAsync(
                    $"{tail} lost sight of {name}{where}; searching the area around where it was heading.",
                    $"UAV {tail}'s onboard agent lost sight of {name}{where}. It is searching the area around where the target " +
                    "should be by now (the search grows with time); nothing else has been done.",
                    new OperatorVoice($"{tail} lost sight of the {target} and is searching for it."));
                break;
            case MissionEventKinds.SearchResumed:
                await notifier.PostAsync(
                    $"{tail} couldn't find {name} again; back to searching {ZoneText(report.ZoneName)}.",
                    $"UAV {tail}'s onboard agent gave up looking for {name} after losing it, and the UAV went back to its search " +
                    $"route over {ZoneText(report.ZoneName)}; the search keeps repeating until the operator stops it.",
                    new OperatorVoice($"{tail} couldn't find the {target} again; back to searching."));
                break;
            case MissionEventKinds.TargetRegained:
                await notifier.PostAsync(
                    $"{tail} found {name} again and is following it.",
                    $"UAV {tail}'s onboard agent found {name} again (checked up close) and the UAV follows it again.",
                    new OperatorVoice($"{tail} found the {target} again."));
                await notifier.SetOperatorUavAsync(tail);
                break;
        }
    }

    /// <summary>
    /// A search goes over its zone again and again until the operator stops it (a moving target may
    /// not be there on one pass). After each pass: what it has found so far, and that it goes on.
    /// </summary>
    private async Task HandlePassCompletedAsync(MissionEventReport report)
    {
        int pass;
        lock (_lock)
            _passesByMission[report.MissionId] = pass = _passesByMission.GetValueOrDefault(report.MissionId) + 1;
        await FlushSearchAsync(SearchIdOf(report.MissionId));
        var (detections, prompt) = DetectionsAndPrompt(SearchIdOf(report.MissionId), report.MissionId);
        var zone = ZoneText(report.ZoneName);
        var found = detections.Count == 0 ? $"no {prompt} found yet" : $"{Plural(detections.Count, "detection")} of {prompt} so far";
        await notifier.PostAsync(
            $"{report.TailNumber} searched all of {zone} (pass {pass}): {found}. Searching it again.",
            $"UAV {report.TailNumber} finished pass {pass} over {zone} looking for {prompt} ({found}) and is searching the zone " +
            "again; it keeps going until the operator stops the mission (StopMission) or sends the UAV elsewhere.",
            new OperatorVoice($"{report.TailNumber} finished pass {pass} of {zone}: {found}. Searching again.", VoiceGroup(SearchIdOf(report.MissionId))));
    }

    /// <summary>A team member's mission ended. While others are still searching, a member that
    /// finished gets a short message and one that stopped early only a history note (the operator
    /// redirected it, so they know); the last one to end brings the team's summary.</summary>
    private async Task HandleTeamMemberEndAsync(Team team, MissionEventReport report)
    {
        List<string> stillSearching, finished, stopped;
        lock (_lock)
        {
            stillSearching = team.Members.Where(m => team.EndByMission[m.MissionId] is null).Select(m => m.Tail).ToList();
            finished = team.Members.Where(m => IsCompleted(team.EndByMission[m.MissionId])).Select(m => m.Tail).ToList();
            stopped = team.Members.Where(m => team.EndByMission[m.MissionId] is { } end && !IsCompleted(end)).Select(m => m.Tail).ToList();
        }
        var zone = ZoneText(report.ZoneName);
        var tail = report.TailNumber;

        if (stillSearching.Count > 0)
        {
            var others = $"{JoinAnd(stillSearching)} {(stillSearching.Count == 1 ? "is" : "are")} still searching";
            if (IsCompleted(report.Kind))
            {
                await notifier.PostAsync(
                    $"{tail} finished its part of the {zone} search; {others}.",
                    $"UAV {tail} flew its whole part of the team search of {zone}; {others} their parts.",
                    new OperatorVoice($"{tail} finished its part of {zone}; {others}.", VoiceGroup(team.Id)));
            }
            else
            {
                await notifier.AddHistoryNoteAsync(
                    $"UAV {tail} stopped searching its part of the team search of {zone} before it finished (it was redirected " +
                    $"or given a new search), so that part of the zone is not fully searched. Nothing was re-planned; {others} their parts.",
                    $"{tail} stopped searching its part of {zone} before finishing.");
            }
            return;
        }

        await FlushSearchAsync(team.Id);
        var (detections, prompt) = DetectionsAndPrompt(team.Id, report.MissionId);
        var everyone = JoinAnd(team.Members.Select(m => m.Tail).ToList());
        if (finished.Count == 0)
        {
            await notifier.AddHistoryNoteAsync(
                $"The team search of {zone} for {prompt} by UAVs {everyone} is over: every UAV stopped before finishing its part " +
                "(redirected or given a new search).",
                $"The team search of {zone} stopped before finishing.");
            return;
        }

        var found = detections.Count == 0
            ? $"no {prompt} found"
            : $"{Plural(detections.Count, "detection")} of {prompt}, the last at {Coordinates(detections[^1])}";
        var spokenFound = detections.Count == 0 ? $"no {prompt} found" : $"{Plural(detections.Count, "detection")} of {prompt}";
        var unfinished = stopped.Count == 0
            ? ""
            : $" {JoinAnd(stopped.Select(t => t + "'s").ToList())} part{(stopped.Count == 1 ? " was" : "s were")} not finished, " +
              "so the zone is not fully searched.";
        var stoppedNote = stopped.Count == 0
            ? ""
            : $"; {JoinAnd(stopped)} stopped early, so {(stopped.Count == 1 ? "its part is" : "their parts are")} not fully searched";
        await notifier.PostAsync(
            $"The team search of {zone} ({everyone}) finished - {found}.{unfinished}",
            $"The team search of {zone} for {prompt} by UAVs {everyone} is over: {JoinAnd(finished)} flew " +
            $"{(finished.Count == 1 ? "its whole part" : "their whole parts")}{stoppedNote}. " +
            (detections.Count == 0 ? "Nothing was found." : $"{Plural(detections.Count, "detection")} in all."),
            new OperatorVoice($"The team search of {zone} finished: {spokenFound}.{unfinished}"));
    }

    /// <summary>A search's detections (by <paramref name="searchId"/>) in order, and what it was
    /// looking for (by the mission that just ended).</summary>
    private (List<DetectionReport> Detections, string Prompt) DetectionsAndPrompt(string searchId, string missionId)
    {
        lock (_lock)
        {
            var detections = _detectionsByMission
                .Where(kv => kv.Key.StartsWith(searchId + "\n", StringComparison.Ordinal))
                .SelectMany(kv => kv.Value)
                .OrderBy(d => d.DetectedAtUtc)
                .ToList();
            return (detections, _promptByMission.GetValueOrDefault(missionId) ?? "the search target");
        }
    }

    private static bool IsCompleted(string? kind) => string.Equals(kind, MissionEventKinds.Completed, StringComparison.OrdinalIgnoreCase);

    /// <summary>"998", "998 and 999", "997, 998 and 999".</summary>
    private static string JoinAnd(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>"UAV 998's onboard agent", or "The onboard agents of UAVs 998 and 999".</summary>
    private static string OnboardAgents(string finders) =>
        finders.Contains(" and ", StringComparison.Ordinal) ? $"The onboard agents of UAVs {finders}" : $"UAV {finders}'s onboard agent";

    /// <summary>"White van detected at 31.81234, 34.66123 by 997 while searching ZoneA (confidence 87%)."</summary>
    public static string DescribeDetection(DetectionReport report)
    {
        var target = report.Prompt.Trim();
        target = char.ToUpperInvariant(target[0]) + target[1..];
        return string.Create(CultureInfo.InvariantCulture,
            $"{target} detected at {Coordinates(report)} by {report.TailNumber} while searching {ZoneText(report.ZoneName)} " +
            $"(confidence {report.Confidence * 100:F0}%).");
    }

    private static string VoiceGroup(string searchId) => "detections:" + searchId;

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

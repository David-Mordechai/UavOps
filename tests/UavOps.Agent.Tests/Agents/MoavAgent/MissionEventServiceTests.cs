using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpMoav;
using UavOps.Agent.Mission;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

/// <summary>McpMoav's handling of what the fleet app reports unprompted: dedupe, the fixed
/// operator wording, history notes, and detection names becoming targetable.</summary>
public class MissionEventServiceTests
{
    private readonly RecordingOperatorNotifier _notifier = new();
    private readonly DetectionPointRegistry _points = new();
    private readonly MissionEventService _sut;

    public MissionEventServiceTests()
    {
        _sut = new MissionEventService(_notifier, _points, new MissionOptions { DetectionDedupeRadiusMeters = 100 },
            NullLogger<MissionEventService>.Instance);
    }

    private static DetectionReport Van(double lat = 31.81234, double lng = 34.66123, string missionId = "m1", string? trackId = null) =>
        new("997", missionId, "ZoneA", "white van", "van", 0.87, lat, lng, DateTime.UtcNow, trackId ?? Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Detection_PostsAFixedMessageWithTheCoordinates()
    {
        (await _sut.HandleDetectionAsync(Van())).Should().BeTrue();

        _notifier.Posted.Select(p => p.Message).Should().Equal(
            "Detection 1: White van detected at 31.81234, 34.66123 by 997 while searching ZoneA (confidence 87%).");
    }

    [Fact]
    public async Task Detection_HistoryNote_SaysNothingWasDone_AndGivesTheLatLngToUse()
    {
        await _sut.HandleDetectionAsync(Van());

        _notifier.Posted[0].HistoryNote.Should()
            .Contain("nothing else has been done").And
            .Contain("location '31.81234,34.66123'");
    }

    [Fact]
    public async Task SameObjectSeenAgainNearby_IsNotReportedTwice()
    {
        await _sut.HandleDetectionAsync(Van());

        // ~30 m away: the same van.
        (await _sut.HandleDetectionAsync(Van(lat: 31.81261))).Should().BeFalse();

        _notifier.Posted.Should().HaveCount(1);
    }

    [Fact]
    public async Task FarAway_OrAnotherMission_IsANewDetection()
    {
        await _sut.HandleDetectionAsync(Van());

        (await _sut.HandleDetectionAsync(Van(lat: 31.8150))).Should().BeTrue();   // ~300 m away
        (await _sut.HandleDetectionAsync(Van(missionId: "m2"))).Should().BeTrue(); // same spot, new mission

        _notifier.Posted.Should().HaveCount(3);
    }

    [Fact]
    public async Task Detection_IsTargetableByNameAndNumber()
    {
        await _sut.HandleDetectionAsync(Van());

        _points.TryResolve("the white van", out var byName).Should().BeTrue();
        _points.TryResolve("Detection 1", out var byNumber).Should().BeTrue();
        byName.Should().Be("31.81234,34.66123").And.Be(byNumber);
    }

    [Fact]
    public async Task MissionCompleted_WithNothingFound_SaysSo()
    {
        _sut.RememberSearchTarget(new SearchTargetRequest("m1", "ZoneA", "white van", 0.5));

        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "m1", "ZoneA", MissionEventKinds.Completed));

        _notifier.Posted.Select(p => p.Message).Should().Equal("997 finished searching ZoneA - no white van found.");
    }

    [Fact]
    public async Task MissionCompleted_AfterDetections_Summarizes()
    {
        await _sut.HandleDetectionAsync(Van());

        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "m1", "ZoneA", MissionEventKinds.Completed));

        _notifier.Posted[^1].Message.Should().Be("997 finished searching ZoneA - 1 detection of white van, the last at 31.81234, 34.66123.");
    }

    // ~300 m apart along a line: each one a separate object.
    private static DetectionReport VanNumber(int i, double confidence = 0.87) =>
        Van(lat: 31.81234 + i * 0.003) with { Confidence = confidence };

    [Fact]
    public async Task AFlood_PostsTheFirstThree_ThenOneNumberedSummaryWhenDue()
    {
        var t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 8; i++)
            (await _sut.HandleDetectionAsync(VanNumber(i, confidence: 0.6 + i * 0.04), t0.AddSeconds(i))).Should().BeTrue();

        _notifier.Posted.Should().HaveCount(3, "the first three are posted as they come");
        _notifier.Posted.Select(p => p.Message).Should().AllSatisfy(m => m.Should().StartWith("Detection "));

        await _sut.FlushDueSummariesAsync(t0.AddSeconds(20));
        _notifier.Posted.Should().HaveCount(3, "the summary isn't due yet");

        await _sut.FlushDueSummariesAsync(t0.AddSeconds(40));
        _notifier.Posted.Should().HaveCount(4);
        var (message, note) = _notifier.Posted[^1];
        message.Should().StartWith("Detections 4-8: 5 more white vans found by 997 while searching ZoneA. Most confident: #8 at");
        note.Should().Contain("detection 4 at location").And.Contain("detection 8 at location").And.Contain("nothing else has been done");

        _points.TryResolve("Detection 6", out _).Should().BeTrue("a detection held for a summary is targetable straight away");
    }

    [Fact]
    public async Task ASummaryListsTheMostConfident_AndCountsTheRest()
    {
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 12; i++)
            await _sut.HandleDetectionAsync(VanNumber(i), t0);

        await _sut.FlushDueSummariesAsync(t0, force: true);

        _notifier.Posted[^1].Message.Should().EndWith("; and 4 more.");
    }

    [Fact]
    public async Task MissionEnd_PostsWhatIsHeld_BeforeTheEnd()
    {
        for (var i = 0; i < 5; i++)
            await _sut.HandleDetectionAsync(VanNumber(i));

        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "m1", "ZoneA", MissionEventKinds.Completed));

        _notifier.Posted.Select(p => p.Message).Should().SatisfyRespectively(
            m => m.Should().StartWith("Detection 1:"),
            m => m.Should().StartWith("Detection 2:"),
            m => m.Should().StartWith("Detection 3:"),
            m => m.Should().StartWith("Detections 4-5:"),
            m => m.Should().StartWith("997 finished searching ZoneA - 5 detections of white van"));
    }

    [Fact]
    public async Task TheVoiceGetsAShortForm_WithoutCoordinates_GroupedByMission()
    {
        for (var i = 0; i < 5; i++)
            await _sut.HandleDetectionAsync(VanNumber(i));
        await _sut.FlushDueSummariesAsync(DateTime.UtcNow, force: true);

        _notifier.Voices.Select(v => v!.Spoken).Should().Equal(
            "Detection 1: white van, by 997.",
            "Detection 2: white van, by 997.",
            "Detection 3: white van, by 997.",
            "Detections 4 to 5: 2 more white vans, by 997.");
        _notifier.Voices.Select(v => v!.Group).Should().AllBe("detections:m1");
    }

    [Fact]
    public async Task MissionAborted_OnlyNotesItInHistory()
    {
        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "m1", "ZoneA", MissionEventKinds.Aborted));

        _notifier.Posted.Should().BeEmpty();
        _notifier.HistoryNotes.Should().ContainSingle();
    }

    // ----- A detection that drives -----

    [Fact]
    public async Task ADetectionSeenAgainFarther_UnderItsTrack_IsAMoveNotANewDetection()
    {
        var t0 = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        await _sut.HandleDetectionAsync(Van(trackId: "trk-1") with { DetectedAtUtc = t0 }, t0);

        // 500 m north, 45 s later: ~40 km/h.
        var moved = Van(lat: 31.81234 + 500 / 111195.0, trackId: "trk-1") with { DetectedAtUtc = t0.AddSeconds(45) };
        (await _sut.HandleDetectionAsync(moved, t0.AddSeconds(45))).Should().BeTrue();

        _notifier.Posted.Should().HaveCount(2);
        _notifier.Posted[1].Message.Should().StartWith("Detection 1 (white van) seen again by 997").And.Contain("~40 km/h N");
        _notifier.Posted[1].HistoryNote.Should().Contain("latest known location").And.Contain("nothing has been done");
        _points.TryResolve("the white van", out var latLng).Should().BeTrue();
        latLng.Should().Be(DetectionPointRegistry.FormatLatLng(moved.Lat, moved.Lng), "\"send 998 to the white van\" goes where it is now");
        _points.TryResolve("detection 1", out var byNumber).Should().BeTrue();
        byNumber.Should().Be(latLng);
    }

    [Fact]
    public async Task AMovingDetection_TellsTheOperatorAtMostEvery30s_ButAlwaysNotesIt()
    {
        var t0 = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        await _sut.HandleDetectionAsync(Van(trackId: "trk-1") with { DetectedAtUtc = t0 }, t0);
        for (var i = 1; i <= 4; i++)
        {
            var at = t0.AddSeconds(i * 10);
            await _sut.HandleDetectionAsync(Van(lat: 31.81234 + i * 150 / 111195.0, trackId: "trk-1") with { DetectedAtUtc = at }, at);
        }

        _notifier.Posted.Should().HaveCount(3, "the detection, then moved at 10 s and again at 40 s (30 s later); 20 s and 30 s were too soon");
        _notifier.HistoryNotes.Should().HaveCount(2, "the 20 s and 30 s sightings still update the model's history");
    }

    [Fact]
    public async Task ATargetBeingFollowed_IsNotAnnouncedOnEveryMove_OnlyLockLostAndRegained()
    {
        var t0 = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        await _sut.HandleDetectionAsync(Van(trackId: "trk-1") with { DetectedAtUtc = t0 }, t0);
        var missionId = Van().MissionId;
        await _sut.HandleMissionEventAsync(new MissionEventReport("997", missionId, "ZoneA", MissionEventKinds.Tracking));
        for (var i = 1; i <= 4; i++)
        {
            var at = t0.AddSeconds(i * 40);
            await _sut.HandleDetectionAsync(Van(lat: 31.81234 + i * 150 / 111195.0, trackId: "trk-1") with { DetectedAtUtc = at }, at);
        }

        _notifier.Posted.Select(p => p.Message).Should().HaveCount(2).And.Contain(m => m.Contains("locked on detection 1"));
        _notifier.OperatorUavs.Should().Equal(["997"], "after a lock, \"stop tracking\" means the tracker without asking which UAV");
        _notifier.HistoryNotes.Should().HaveCount(4, "every position still reaches the model's history");
        _points.TryResolve("the white van", out var latLng).Should().BeTrue();
        latLng.Should().Be(DetectionPointRegistry.FormatLatLng(31.81234 + 600 / 111195.0, Van().Lng), "\"send 998 to the white van\" goes where it is now");

        // Lost: moving updates are announced again (rate-limited) - nobody is following it.
        await _sut.HandleMissionEventAsync(new MissionEventReport("997", missionId, "ZoneA", MissionEventKinds.TargetLost));
        var later = t0.AddSeconds(400);
        await _sut.HandleDetectionAsync(Van(lat: 31.81234 + 900 / 111195.0, trackId: "trk-1") with { DetectedAtUtc = later }, later);
        _notifier.Posted.Last().Message.Should().Contain("seen again");
    }

    [Fact]
    public async Task ATrackReportedAgainInPlace_IsTheSameSighting()
    {
        await _sut.HandleDetectionAsync(Van(trackId: "trk-1"));

        (await _sut.HandleDetectionAsync(Van(lat: 31.81250, trackId: "trk-1"))).Should().BeFalse("~18 m: not a move");

        _notifier.Posted.Should().ContainSingle();
        _notifier.HistoryNotes.Should().BeEmpty();
    }

    // ----- Team searches: one zone split between 997 (mission a) and 998 (mission b) -----

    private void RememberTeam() => _sut.RememberTeam("t1", [("997", "a"), ("998", "b")]);

    private static DetectionReport Car(string tail, string missionId, double lat = 31.81234) =>
        new(tail, missionId, "ZoneA", "red car", "car", 0.9, lat, 34.66123, DateTime.UtcNow, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Team_TheSameObjectSeenByTwoMembers_IsReportedOnce()
    {
        RememberTeam();

        (await _sut.HandleDetectionAsync(Car("997", "a"))).Should().BeTrue();
        (await _sut.HandleDetectionAsync(Car("998", "b"))).Should().BeFalse();

        _notifier.Posted.Should().ContainSingle();
    }

    [Fact]
    public async Task Team_TheIndividualMessagesAreCountedPerTeam_NotPerUav()
    {
        RememberTeam();

        // Four different cars (~300 m apart), two per UAV: the first 3 of the TEAM are posted.
        await _sut.HandleDetectionAsync(Car("997", "a", 31.810));
        await _sut.HandleDetectionAsync(Car("998", "b", 31.813));
        await _sut.HandleDetectionAsync(Car("997", "a", 31.816));
        await _sut.HandleDetectionAsync(Car("998", "b", 31.819));

        _notifier.Posted.Should().HaveCount(3);
        _notifier.Voices.Select(v => v!.Group).Should().AllBe("detections:team:t1");
    }

    [Fact]
    public async Task Team_AMemberFinishingFirst_GetsAShortMessage_AndTheLastOneTheTeamSummary()
    {
        RememberTeam();
        await _sut.HandleDetectionAsync(Car("998", "b"));

        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "a", "ZoneA", MissionEventKinds.Completed));
        _notifier.Posted.Select(p => p.Message).Last().Should().Be("997 finished its part of the ZoneA search; 998 is still searching.");

        await _sut.HandleMissionEventAsync(new MissionEventReport("998", "b", "ZoneA", MissionEventKinds.Completed));
        _notifier.Posted.Select(p => p.Message).Last().Should().Be(
            "The team search of ZoneA (997 and 998) finished - 1 detection of red car, the last at 31.81234, 34.66123.");
    }

    [Fact]
    public async Task Team_AMemberThatStoppedEarly_IsANoteThen_TheSummarySaysItsPartWasNotFinished()
    {
        _sut.RememberTeam("t1", [("997", "a"), ("998", "b")]);
        _sut.RememberSearchTarget(new SearchTargetRequest("b", "ZoneA", "red car", 0.5));

        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "a", "ZoneA", MissionEventKinds.Aborted));
        _notifier.Posted.Should().BeEmpty();
        _notifier.HistoryNotes.Should().ContainSingle().Which.Note.Should().Contain("not fully searched").And.Contain("Nothing was re-planned");

        await _sut.HandleMissionEventAsync(new MissionEventReport("998", "b", "ZoneA", MissionEventKinds.Completed));
        _notifier.Posted.Should().ContainSingle().Which.Message.Should().Be(
            "The team search of ZoneA (997 and 998) finished - no red car found. 997's part was not finished, so the zone is not fully searched.");
    }

    [Fact]
    public async Task Team_EveryMemberStoppedEarly_OnlyNotesIt()
    {
        RememberTeam();

        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "a", "ZoneA", MissionEventKinds.Aborted));
        await _sut.HandleMissionEventAsync(new MissionEventReport("998", "b", "ZoneA", MissionEventKinds.Aborted));

        _notifier.Posted.Should().BeEmpty();
        _notifier.HistoryNotes.Should().HaveCount(2);
    }
}

/// <summary>Records what McpMoav would have posted to the host.</summary>
internal sealed class RecordingOperatorNotifier : IOperatorNotifier
{
    public List<(string Message, string? HistoryNote)> Posted { get; } = [];
    public List<(string Note, string Message)> HistoryNotes { get; } = [];

    public List<OperatorVoice?> Voices { get; } = [];

    public Task PostAsync(string message, string? historyNote, OperatorVoice? voice = null)
    {
        Posted.Add((message, historyNote));
        Voices.Add(voice);
        return Task.CompletedTask;
    }

    public Task AddHistoryNoteAsync(string note, string message)
    {
        HistoryNotes.Add((note, message));
        return Task.CompletedTask;
    }

    public List<string> OperatorUavs { get; } = [];

    public Task SetOperatorUavAsync(string tailNumber)
    {
        OperatorUavs.Add(tailNumber);
        return Task.CompletedTask;
    }
}

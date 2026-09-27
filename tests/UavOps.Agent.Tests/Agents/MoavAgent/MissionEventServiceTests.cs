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

    private static DetectionReport Van(double lat = 31.81234, double lng = 34.66123, string missionId = "m1") =>
        new("997", missionId, "ZoneA", "white van", "van", 0.87, lat, lng, DateTime.UtcNow, "t1");

    [Fact]
    public async Task Detection_PostsAFixedMessageWithTheCoordinates()
    {
        (await _sut.HandleDetectionAsync(Van())).Should().BeTrue();

        _notifier.Posted.Select(p => p.Message).Should().Equal(
            "White van detected at 31.81234, 34.66123 by 997 while searching ZoneA (confidence 87%).");
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

    [Fact]
    public async Task MissionAborted_OnlyNotesItInHistory()
    {
        await _sut.HandleMissionEventAsync(new MissionEventReport("997", "m1", "ZoneA", MissionEventKinds.Aborted));

        _notifier.Posted.Should().BeEmpty();
        _notifier.HistoryNotes.Should().ContainSingle();
    }
}

/// <summary>Records what McpMoav would have posted to the host.</summary>
internal sealed class RecordingOperatorNotifier : IOperatorNotifier
{
    public List<(string Message, string? HistoryNote)> Posted { get; } = [];
    public List<(string Note, string Message)> HistoryNotes { get; } = [];

    public Task PostAsync(string message, string? historyNote)
    {
        Posted.Add((message, historyNote));
        return Task.CompletedTask;
    }

    public Task AddHistoryNoteAsync(string note, string message)
    {
        HistoryNotes.Add((note, message));
        return Task.CompletedTask;
    }
}

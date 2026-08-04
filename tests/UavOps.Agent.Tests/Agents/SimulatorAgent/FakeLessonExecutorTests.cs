using FluentAssertions;
using UavOps.Agent.Contracts;
using UavOps.Agent.Simulator.Fake;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class FakeLessonExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_KnownLesson_ReturnsSucceeded()
    {
        var sut = new FakeLessonExecutor();

        var (outcome, detail) = await sut.ExecuteAsync(FakeLessonExecutor.FakeLessons[0], CancellationToken.None);

        outcome.Should().Be(LessonOutcome.Succeeded);
        detail.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownLesson_ReturnsFailed()
    {
        var sut = new FakeLessonExecutor();

        var (outcome, detail) = await sut.ExecuteAsync("does-not-exist.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.Failed);
        detail.Should().Contain("does-not-exist.ps1");
    }
}

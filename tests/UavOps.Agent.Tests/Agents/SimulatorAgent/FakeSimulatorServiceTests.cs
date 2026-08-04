using FluentAssertions;
using NSubstitute;
using UavOps.Agent.Contracts;
using UavOps.Agent.Simulator.Fake;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class FakeSimulatorServiceTests
{
    private static FakeSimulatorService CreateSut(ISimulatorLessonJobQueue? queue = null) =>
        new(queue ?? Substitute.For<ISimulatorLessonJobQueue>());

    [Fact]
    public async Task EnsureVmwareHostRunning_FirstCall_ReportsStarted_SecondCall_ReportsAlreadyRunning()
    {
        var sut = CreateSut();

        var first = await sut.EnsureVmwareHostRunning(CancellationToken.None);
        var second = await sut.EnsureVmwareHostRunning(CancellationToken.None);

        first.Success.Should().BeTrue();
        first.Value.Should().BeEquivalentTo(new { started = true, alreadyRunning = false, ready = true });
        second.Value.Should().BeEquivalentTo(new { started = false, alreadyRunning = true, ready = true });
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_IsIndependentOfHostState()
    {
        var sut = CreateSut();
        await sut.EnsureVmwareHostRunning(CancellationToken.None);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ListSimulatorLessons_ReturnsFixedNonEmptyList()
    {
        var sut = CreateSut();

        var result = await sut.ListSimulatorLessons(CancellationToken.None);

        result.Success.Should().BeTrue();
        var lessons = result.Value.Should().BeAssignableTo<List<string>>().Subject;
        lessons.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunSimulatorLesson_EnqueuesJob_AndReturnsQueuedImmediately()
    {
        var queue = Substitute.For<ISimulatorLessonJobQueue>();
        var sut = CreateSut(queue);

        var result = await sut.RunSimulatorLesson("intro-flight-basics.ps1", CancellationToken.None);

        result.Success.Should().BeTrue();
        queue.Received(1).Enqueue(Arg.Is<SimulatorLessonJob>(j => j!.LessonName == "intro-flight-basics.ps1"));
    }
}

using FluentAssertions;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class SimulatorLessonJobQueueTests
{
    [Fact]
    public async Task Enqueue_ThenReadAllAsync_YieldsTheJob()
    {
        var sut = new SimulatorLessonJobQueue();
        var job = new SimulatorLessonJob("lesson1.ps1", "corr1", DateTimeOffset.UtcNow);

        sut.Enqueue(job);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = sut.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().Be(job);
    }

    [Fact]
    public async Task MultipleEnqueues_ComeOutInOrder()
    {
        var sut = new SimulatorLessonJobQueue();
        var first = new SimulatorLessonJob("first.ps1", "corr1", DateTimeOffset.UtcNow);
        var second = new SimulatorLessonJob("second.ps1", "corr2", DateTimeOffset.UtcNow);

        sut.Enqueue(first);
        sut.Enqueue(second);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = sut.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await enumerator.MoveNextAsync();
        enumerator.Current.Should().Be(first);
        await enumerator.MoveNextAsync();
        enumerator.Current.Should().Be(second);
    }
}

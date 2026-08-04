using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class LocalLessonExecutorTests
{
    private static LocalLessonExecutor CreateSut(ILocalLessonRunner? lessonRunner = null) =>
        new(lessonRunner ?? Substitute.For<ILocalLessonRunner>(), NullLogger<LocalLessonExecutor>.Instance);

    [Fact]
    public async Task ExecuteAsync_CleanExit_ReturnsSucceeded()
    {
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync("lesson1.ps1", Arg.Any<CancellationToken>()).Returns((0, "lesson complete", ""));
        var sut = CreateSut(runner);

        var (outcome, detail) = await sut.ExecuteAsync("lesson1.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.Succeeded);
        detail.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ReturnsFailed()
    {
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync("lesson1.ps1", Arg.Any<CancellationToken>()).Returns((1, "", "script threw"));
        var sut = CreateSut(runner);

        var (outcome, detail) = await sut.ExecuteAsync("lesson1.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.Failed);
        detail.Should().Contain("script threw");
    }

    [Fact]
    public async Task ExecuteAsync_RestartingContainer_ReturnsSucceededWithWarnings()
    {
        // Regression test for a real production case: the script itself exits 0, but a
        // container is crash-looping ("Restarting (127)") — this must surface as a warning, not
        // get lost in the raw output where the model previously missed it.
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync("lesson1.ps1", Arg.Any<CancellationToken>())
            .Returns((0, "abc123   some-image   \"cmd\"   1m ago   Restarting (127) 2 seconds ago   my-container-1\n", ""));
        var sut = CreateSut(runner);

        var (outcome, detail) = await sut.ExecuteAsync("lesson1.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.SucceededWithWarnings);
        detail.Should().Contain("my-container-1");
    }

    [Fact]
    public async Task ExecuteAsync_ExitedZero_IsNotFlaggedAsUnhealthy()
    {
        // "Exited (0)" is the normal, intended state for a one-shot/init container — must not be
        // treated the same as a real failure.
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync("lesson1.ps1", Arg.Any<CancellationToken>())
            .Returns((0, "abc123   some-image   \"cmd\"   1m ago   Exited (0) 2 seconds ago   init-container-1\n", ""));
        var sut = CreateSut(runner);

        var (outcome, _) = await sut.ExecuteAsync("lesson1.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_ExitedNonZero_ReturnsSucceededWithWarnings()
    {
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync("lesson1.ps1", Arg.Any<CancellationToken>())
            .Returns((0, "abc123   some-image   \"cmd\"   1m ago   Exited (1) 2 seconds ago   failed-container-1\n", ""));
        var sut = CreateSut(runner);

        var (outcome, detail) = await sut.ExecuteAsync("lesson1.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.SucceededWithWarnings);
        detail.Should().Contain("failed-container-1");
    }

    [Fact]
    public async Task ExecuteAsync_RealProductionOutputExcerpt_DetectsTheTwoCrashLoopingContainers()
    {
        // Trimmed excerpt of an actual `restart-MultiUav.ps1` run's output (encoderstub
        // containers stuck restarting with exit 127), alongside plenty of healthy "Up ..." lines
        // that must NOT be flagged.
        const string realOutput =
            "a9a354f89f98   rt-srv:443/.../orbiter4mk2_iointerface   \"bash -c ...\"   22 seconds ago   Up Less than a second   air1-orbiter4mk2iol-998-1\n" +
            "fbcd3cd25e50   rt-srv:443/.../encoderstub                \"bash -c ...\"   44 seconds ago   Restarting (127) Less than a second ago   air1-encoder-998-1\n" +
            "0bb132149e68   rt-srv:443/.../encoderstub                \"bash -c ...\"   About a minute ago   Restarting (127) 2 seconds ago   air0-encoder-999-1\n" +
            "c73836a963a9   rt-srv:443/.../gcs2_rtc                   \"bash -c ...\"   2 minutes ago   Up About a minute   multiuav-rtc1-1\n";
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync("restart-MultiUav.ps1", Arg.Any<CancellationToken>()).Returns((0, realOutput, ""));
        var sut = CreateSut(runner);

        var (outcome, detail) = await sut.ExecuteAsync("restart-MultiUav.ps1", CancellationToken.None);

        outcome.Should().Be(LessonOutcome.SucceededWithWarnings);
        detail.Should().Contain("air1-encoder-998-1").And.Contain("air0-encoder-999-1");
        detail.Should().NotContain("multiuav-rtc1-1"); // healthy container must not appear
    }

    [Fact]
    public async Task ExecuteAsync_RunnerThrows_PropagatesException()
    {
        // e.g. LocalLessonRunner's path-traversal guard throwing for an out-of-folder lesson name
        // — the caller (SimulatorLessonJobProcessor) is responsible for catching this, not
        // LocalLessonExecutor swallowing it.
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.RunLessonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(int, string, string)>(new InvalidOperationException("resolves outside the configured lessons folder")));
        var sut = CreateSut(runner);

        var act = () => sut.ExecuteAsync("../evil.ps1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside the configured lessons folder*");
    }
}

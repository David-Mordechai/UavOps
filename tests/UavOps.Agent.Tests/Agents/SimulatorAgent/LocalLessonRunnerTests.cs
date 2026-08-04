using FluentAssertions;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class LocalLessonRunnerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("UavOps.LocalLessonRunnerTests.");

    public void Dispose()
    {
        _tempDir.Delete(recursive: true);
    }

    private LocalLessonRunner CreateSut() => new(new SimulatorOptions { LessonsFolder = _tempDir.FullName });

    [Fact]
    public void ListLessons_MissingFolder_ReturnsEmpty()
    {
        var sut = new LocalLessonRunner(new SimulatorOptions { LessonsFolder = Path.Combine(_tempDir.FullName, "does-not-exist") });

        sut.ListLessons().Should().BeEmpty();
    }

    [Fact]
    public void ListLessons_ReturnsOnlyPs1Files_SortedByName()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "b-lesson.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "a-lesson.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "readme.txt"), "not a lesson");
        var sut = CreateSut();

        var lessons = sut.ListLessons();

        lessons.Should().Equal("a-lesson.ps1", "b-lesson.ps1");
    }

    [Fact]
    public async Task RunLessonAsync_KnownLesson_ExecutesAndReturnsExitCode()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "ok.ps1"), "Write-Output 'hello from lesson'; exit 0");
        var sut = CreateSut();

        var (exitCode, output, _) = await sut.RunLessonAsync("ok.ps1", CancellationToken.None);

        exitCode.Should().Be(0);
        output.Should().Contain("hello from lesson");
    }

    [Fact]
    public async Task RunLessonAsync_ScriptExitsNonZero_ReturnsThatExitCode()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "fails.ps1"), "exit 7");
        var sut = CreateSut();

        var (exitCode, _, _) = await sut.RunLessonAsync("fails.ps1", CancellationToken.None);

        exitCode.Should().Be(7);
    }

    [Fact]
    public async Task RunLessonAsync_UnknownLesson_ThrowsRatherThanRunningAnything()
    {
        var sut = CreateSut();

        var act = () => sut.RunLessonAsync("does-not-exist.ps1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task RunLessonAsync_PathTraversalAttempt_ThrowsRatherThanEscapingLessonsFolder()
    {
        // A chosen "lesson name" ultimately originates from the operator's chat reply — must
        // never be trusted as a bare filename relative to LessonsFolder.
        var outsideFile = Path.Combine(_tempDir.Parent!.FullName, "outside.ps1");
        File.WriteAllText(outsideFile, "Write-Output 'should never run'");
        try
        {
            var sut = CreateSut();

            var act = () => sut.RunLessonAsync(Path.Combine("..", Path.GetFileName(outsideFile)), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside the configured lessons folder*");
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }
}

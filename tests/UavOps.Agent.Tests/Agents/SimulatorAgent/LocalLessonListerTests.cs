using FluentAssertions;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpSimulator;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class LocalLessonListerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("UavOps.LocalLessonListerTests.");

    public void Dispose()
    {
        _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void ListLessons_MissingFolder_ReturnsEmpty()
    {
        var sut = new LocalLessonLister(new SimulatorOptions { LessonsFolder = Path.Combine(_tempDir.FullName, "does-not-exist") });

        sut.ListLessons().Should().BeEmpty();
    }

    [Fact]
    public void ListLessons_ReturnsOnlyPs1Files_SortedByName()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "b-lesson.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "a-lesson.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "readme.txt"), "not a lesson");
        var sut = new LocalLessonLister(new SimulatorOptions { LessonsFolder = _tempDir.FullName });

        var lessons = sut.ListLessons();

        lessons.Should().Equal("a-lesson.ps1", "b-lesson.ps1");
    }
}

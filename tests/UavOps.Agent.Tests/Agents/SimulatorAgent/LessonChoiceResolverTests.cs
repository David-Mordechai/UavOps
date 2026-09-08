using FluentAssertions;
using UavOps.Agent.McpSimulator;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

/// <summary>
/// Moved from the host's own (now-deleted) <c>AskOperatorChoiceToolTests</c> alongside the logic
/// itself (<see cref="LessonChoiceResolver"/>) when <c>AskOperatorWhichLesson</c> moved fully into
/// <c>UavOps.Agent.McpSimulator</c> - same test cases, new home.
/// </summary>
public class LessonChoiceResolverTests
{
    private static readonly List<string> Lessons = ["intro-flight-basics.ps1", "advanced-navigation.ps1", "emergency-procedures.ps1"];

    [Fact]
    public void TryAutoResolve_OperatorNamedFullFileName_ResolvesDirectly()
    {
        var result = LessonChoiceResolver.TryAutoResolve(Lessons, "run intro-flight-basics.ps1 please");

        result.Should().Be("intro-flight-basics.ps1");
    }

    [Fact]
    public void TryAutoResolve_OperatorNamedFileNameWithoutExtension_ResolvesDirectly()
    {
        var result = LessonChoiceResolver.TryAutoResolve(Lessons, "run the intro-flight-basics lesson");

        result.Should().Be("intro-flight-basics.ps1");
    }

    [Fact]
    public void TryAutoResolve_IsCaseInsensitive()
    {
        var result = LessonChoiceResolver.TryAutoResolve(Lessons, "RUN INTRO-FLIGHT-BASICS");

        result.Should().Be("intro-flight-basics.ps1");
    }

    [Fact]
    public void TryAutoResolve_NoMatch_ReturnsNull()
    {
        var result = LessonChoiceResolver.TryAutoResolve(Lessons, "run a training lesson");

        result.Should().BeNull();
    }

    [Fact]
    public void TryAutoResolve_AmbiguousOperatorTextMentioningNoLessonName_ReturnsNull()
    {
        // Guards against a false match on a generic word ("lesson") that happens to be a
        // substring-adjacent to real lesson names - Lessons above share no common substring with
        // this text, so this should behave identically to the "no match" case, not accidentally
        // match more than one.
        var result = LessonChoiceResolver.TryAutoResolve(Lessons, "run the intro-flight-basics or advanced-navigation lesson");

        result.Should().BeNull();
    }

    [Fact]
    public void TryAutoResolve_EmptyLessonList_ReturnsNull()
    {
        var result = LessonChoiceResolver.TryAutoResolve([], "run intro-flight-basics");

        result.Should().BeNull();
    }
}

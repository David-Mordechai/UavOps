using FluentAssertions;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Tests.Tooling;

public class RetrievalQueryTests
{
    [Fact]
    public void ShortHistory_IsKeptWhole_WithTheTurnLast()
    {
        RetrievalQuery.Build(["Which UAV do you mean?", "998"], "set speed 200", 4000)
            .Should().Be("Which UAV do you mean?\n998\nset speed 200");
    }

    [Fact]
    public void LongHistory_KeepsOnlyTheNewestMessages_WithinTheBudget()
    {
        var history = Enumerable.Range(1, 40).Select(i => $"message {i} " + new string('x', 400)).ToList();

        var query = RetrievalQuery.Build(history, "start the mission", 1000);

        query.Should().EndWith("\nstart the mission");
        query.Length.Should().BeLessThanOrEqualTo(1000 + "\nstart the mission".Length + 2);
        query.Should().Contain("message 40").And.NotContain("message 1 ");
    }

    [Fact]
    public void AMessageLongerThanTheBudget_IsCutToItsEnd()
    {
        var query = RetrievalQuery.Build(["start-" + new string('a', 5000) + "-end"], "go", 100);

        query.Should().Be(new string('a', 96) + "-end\ngo");
    }

    [Fact]
    public void TheTurnIsAlwaysThere_EvenWithNoBudget()
    {
        RetrievalQuery.Build(["anything"], "bring them home", 0).Should().Be("bring them home");
    }
}

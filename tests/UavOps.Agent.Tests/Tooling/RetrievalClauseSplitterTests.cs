using FluentAssertions;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Tooling;

public class RetrievalClauseSplitterTests
{
    [Fact]
    public void Split_TheReportedCompoundTurn_SeparatesTheReturnHomeAskFromTheSummaryAsk()
    {
        RetrievalClauseSplitter.Split("bring them all home and give me full summary of today session")
            .Should().Equal("bring them all home", "give me full summary of today session");
    }

    [Theory]
    [InlineData("bring 997 home, point 998 payload at alpha and give me 999 link status",
        new[] { "bring 997 home", "point 998 payload at alpha", "give me 999 link status" })]
    [InlineData("fly 997 to alpha then set its speed to 250", new[] { "fly 997 to alpha", "set its speed to 250" })]
    [InlineData("restart the telemetry service; also tell me the health", new[] { "restart the telemetry service", "tell me the health" })]
    public void Split_SeparatesOnEverySupportedConnector(string text, string[] expected)
    {
        RetrievalClauseSplitter.Split(text).Should().Equal(expected);
    }

    [Theory]
    [InlineData("What UAVs do we have?")]
    [InlineData("999")]
    [InlineData("bring them all home")]
    public void Split_SingleAsk_ReturnsNothing(string text)
    {
        RetrievalClauseSplitter.Split(text).Should().BeEmpty();
    }

    [Fact]
    public void Split_DropsOneWordFragments()
    {
        // "report" alone is too little to rank on; with only one real clause left there's nothing
        // to add beyond the whole-turn ranking, so nothing is returned.
        RetrievalClauseSplitter.Split("bring them home and report").Should().BeEmpty();
    }

    [Fact]
    public void Split_DoesNotSplitInsideWords()
    {
        // "band", "thenceforth", "Alsop" contain connector words but aren't connectors.
        RetrievalClauseSplitter.Split("check the band status of Alsop station")
            .Should().BeEmpty();
    }
}

using FluentAssertions;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class ChatConfirmationParserTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes")]
    [InlineData("YES!")]
    [InlineData("y")]
    [InlineData("yep")]
    [InlineData("sure")]
    [InlineData("go ahead")]
    [InlineData("proceed.")]
    public void TryParse_RecognizesAffirmativeReplies(string reply)
    {
        var parsed = ChatConfirmationParser.TryParse(reply, out var approved);

        parsed.Should().BeTrue();
        approved.Should().BeTrue();
    }

    [Theory]
    [InlineData("no")]
    [InlineData("No")]
    [InlineData("NO!")]
    [InlineData("n")]
    [InlineData("nope")]
    [InlineData("cancel")]
    [InlineData("abort.")]
    public void TryParse_RecognizesNegativeReplies(string reply)
    {
        var parsed = ChatConfirmationParser.TryParse(reply, out var approved);

        parsed.Should().BeTrue();
        approved.Should().BeFalse();
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("what will it do")]
    [InlineData("")]
    [InlineData("set speed to 200")]
    public void TryParse_UnrecognizedText_ReturnsFalse(string reply)
    {
        var parsed = ChatConfirmationParser.TryParse(reply, out _);

        parsed.Should().BeFalse();
    }
}

using FluentAssertions;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

public class KnownPointsTests
{
    [Theory]
    [InlineData("target alpha", "alpha")]
    [InlineData("Target Bravo", "Bravo")]
    [InlineData("TARGET   alpha", "alpha")]
    [InlineData("alpha", "alpha")]
    [InlineData("home", "home")]
    public void Canonicalize_StripsLeadingTargetPrefix(string given, string expected) =>
        KnownPoints.Canonicalize(given).Should().Be(expected);

    [Theory]
    [InlineData("alpha")]
    [InlineData("target alpha")]
    [InlineData("Target Alpha")]
    public void TryResolve_AcceptsBothBareAndTargetPrefixedForms(string location)
    {
        var resolved = KnownPoints.TryResolve(location, out var lat, out var lng);

        resolved.Should().BeTrue();
        lat.Should().Be(31.812000);
        lng.Should().Be(34.660000);
    }

    [Fact]
    public void TryResolve_UnknownLocation_ReturnsFalse() =>
        KnownPoints.TryResolve("nowhere", out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("UAV-1 is now flying to target alpha.", "UAV-1 is now flying to alpha.")]
    [InlineData("UAV-1 is now flying to Target Alpha and Target Bravo.", "UAV-1 is now flying to Alpha and Bravo.")]
    [InlineData("Payload pointed at target bravo for UAV-2.", "Payload pointed at bravo for UAV-2.")]
    [InlineData("UAV-1 is now flying to alpha.", "UAV-1 is now flying to alpha.")]
    [InlineData("no location mentioned here", "no location mentioned here")]
    public void CanonicalizeText_StripsTargetPrefixWhereverItAppearsInFreeText(string given, string expected) =>
        KnownPoints.CanonicalizeText(given).Should().Be(expected);
}

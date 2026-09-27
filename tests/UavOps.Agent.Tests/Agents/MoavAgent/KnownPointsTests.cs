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
        lat.Should().Be(31.346500);
        lng.Should().Be(35.050300);
    }

    [Fact]
    public void TryResolve_UnknownLocation_ReturnsFalse() =>
        KnownPoints.TryResolve("nowhere", out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("997 is now flying to target alpha.", "997 is now flying to alpha.")]
    [InlineData("997 is now flying to Target Alpha and Target Bravo.", "997 is now flying to Alpha and Bravo.")]
    [InlineData("Payload pointed at target bravo for 998.", "Payload pointed at bravo for 998.")]
    [InlineData("997 is now flying to alpha.", "997 is now flying to alpha.")]
    [InlineData("no location mentioned here", "no location mentioned here")]
    public void CanonicalizeText_StripsTargetPrefixWhereverItAppearsInFreeText(string given, string expected) =>
        KnownPoints.CanonicalizeText(given).Should().Be(expected);

    [Theory]
    [InlineData("31.81234,34.66123", 31.81234, 34.66123)]
    [InlineData(" -31.5 , 120 ", -31.5, 120)]
    public void TryResolve_AcceptsALatLngLiteral(string text, double lat, double lng)
    {
        KnownPoints.TryResolve(text, out var parsedLat, out var parsedLng).Should().BeTrue();
        parsedLat.Should().Be(lat);
        parsedLng.Should().Be(lng);
    }

    [Theory]
    [InlineData("91,34")]
    [InlineData("31.8,181")]
    [InlineData("31.8 34.6")]
    public void TryResolve_RejectsAnOutOfRangeOrMalformedLatLng(string text) =>
        KnownPoints.TryResolve(text, out _, out _).Should().BeFalse();
}

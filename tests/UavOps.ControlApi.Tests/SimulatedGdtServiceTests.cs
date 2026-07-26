using FluentAssertions;
using UavOps.ControlApi.Services;
using Xunit;

namespace UavOps.ControlApi.Tests;

public class SimulatedGdtServiceTests
{
    private readonly SimulatedUavFleetService _fleet = new();
    private readonly SimulatedGdtService _sut;

    public SimulatedGdtServiceTests()
    {
        _sut = new SimulatedGdtService(_fleet);
    }

    [Fact]
    public void GetLinkStatus_KnownTailNumber_ReturnsDefaultStatus()
    {
        var (found, status) = _sut.GetLinkStatus("UAV-1");

        found.Should().BeTrue();
        status!.LinkState.Should().Be("Connected");
        status.TrackingMode.Should().Be("Auto");
    }

    [Fact]
    public void GetLinkStatus_UnknownTailNumber_ReturnsNotFound()
    {
        var (found, status) = _sut.GetLinkStatus("UAV-99");

        found.Should().BeFalse();
        status.Should().BeNull();
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("manual")]
    [InlineData("Auto")]
    public void SetTrackingMode_ValidMode_Succeeds(string mode)
    {
        var (found, status, error) = _sut.SetTrackingMode("UAV-1", mode);

        found.Should().BeTrue();
        error.Should().BeNull();
        status!.TrackingMode.Should().Be(mode);
    }

    [Fact]
    public void SetTrackingMode_InvalidMode_ReturnsError()
    {
        var (found, status, error) = _sut.SetTrackingMode("UAV-1", "Bogus");

        found.Should().BeTrue();
        status.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void SetTrackingMode_UnknownTailNumber_ReturnsNotFound()
    {
        var (found, status, error) = _sut.SetTrackingMode("UAV-99", "Auto");

        found.Should().BeFalse();
        status.Should().BeNull();
    }
}

using FluentAssertions;
using UavOps.ControlApi.Services;
using Xunit;

namespace UavOps.ControlApi.Tests;

public class SimulatedUavFleetServiceTests
{
    private readonly SimulatedUavFleetService _sut = new();

    [Fact]
    public void ListFleet_ReturnsThreeSeededVehicles()
    {
        var fleet = _sut.ListFleet();

        fleet.Should().HaveCount(3);
        fleet.Select(v => v.TailNumber).Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
    }

    [Fact]
    public void Navigate_KnownLocation_UpdatesPositionAndMode()
    {
        var result = _sut.Navigate("UAV-1", "target alpha");

        result.Success.Should().BeTrue();
        result.Snapshot!.Lat.Should().Be(31.812000);
        result.Snapshot.Lng.Should().Be(34.660000);
        result.Snapshot.Mode.Should().Be("Transiting");
    }

    [Fact]
    public void Navigate_UnknownLocation_ReturnsValidationFailed()
    {
        var result = _sut.Navigate("UAV-1", "nowhere");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(UavCommandError.ValidationFailed);
    }

    [Fact]
    public void Navigate_UnknownTailNumber_ReturnsVehicleNotFound()
    {
        var result = _sut.Navigate("UAV-99", "target alpha");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(UavCommandError.VehicleNotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void SetSpeed_OutOfRange_ReturnsValidationFailed(int speed)
    {
        var result = _sut.SetSpeed("UAV-1", speed);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(UavCommandError.ValidationFailed);
    }

    [Fact]
    public void SetSpeed_ValidRange_UpdatesSpeed()
    {
        var result = _sut.SetSpeed("UAV-1", 200);

        result.Success.Should().BeTrue();
        result.Snapshot!.SpeedKts.Should().Be(200);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void SetAltitude_OutOfRange_ReturnsValidationFailed(int altitude)
    {
        var result = _sut.SetAltitude("UAV-1", altitude);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(UavCommandError.ValidationFailed);
    }

    [Fact]
    public void ReturnToLaunch_SetsReturningMode()
    {
        var result = _sut.ReturnToLaunch("UAV-1");

        result.Success.Should().BeTrue();
        result.Snapshot!.Mode.Should().Be("ReturningToLaunch");
    }

    [Fact]
    public void PointPayload_KnownLocation_LocksPayload()
    {
        var result = _sut.PointPayload("UAV-1", "target bravo");

        result.Success.Should().BeTrue();
        result.Snapshot!.PayloadLockedOn.Should().Be("target bravo");
    }

    [Fact]
    public void ResetPayload_ClearsLock()
    {
        _sut.PointPayload("UAV-1", "target alpha");

        var result = _sut.ResetPayload("UAV-1");

        result.Success.Should().BeTrue();
        result.Snapshot!.PayloadLockedOn.Should().BeNull();
    }

    [Fact]
    public void UploadWaypoints_StoresWaypointsAndReportsCount()
    {
        var waypoints = new List<UavOps.ControlApi.Models.Waypoint>
        {
            new(31.8, 34.6, 3000),
            new(31.9, 34.7, 3500),
        };

        var (found, accepted) = _sut.UploadWaypoints("UAV-1", waypoints);

        found.Should().BeTrue();
        accepted.Should().Be(2);

        var (_, status) = _sut.GetMissionStatus("UAV-1");
        status!.WaypointCount.Should().Be(2);
    }

    [Fact]
    public void MutatingOneVehicle_NeverAffectsAnother()
    {
        _sut.SetSpeed("UAV-2", 300);

        var uav1 = _sut.GetTelemetry("UAV-1");
        var uav3 = _sut.GetTelemetry("UAV-3");

        uav1.Snapshot!.SpeedKts.Should().Be(105);
        uav3.Snapshot!.SpeedKts.Should().Be(105);
    }
}

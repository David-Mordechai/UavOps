using FluentAssertions;
using UavOps.Agent.McpMoav;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

public class SimulatedUavOperationServiceTests
{
    private readonly SimulatedUavOperationService _sut = new();

    [Fact]
    public async Task ListFleet_ReturnsThreeSeededVehicles()
    {
        var result = await _sut.ListFleet(CancellationToken.None);

        var fleet = (List<UavSummary>)result.Value!;
        fleet.Should().HaveCount(3);
        fleet.Select(v => v.TailNumber).Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task Navigate_KnownLocation_UpdatesPositionAndMode()
    {
        var result = await _sut.Navigate("UAV-1", "target alpha", CancellationToken.None);

        result.Success.Should().BeTrue();
        var snapshot = (TelemetrySnapshot)result.Value!;
        snapshot.Lat.Should().Be(31.812000);
        snapshot.Lng.Should().Be(34.660000);
        snapshot.Mode.Should().Be("Transiting");
    }

    [Fact]
    public async Task Navigate_UnknownLocation_ReturnsValidationFailed()
    {
        var result = await _sut.Navigate("UAV-1", "nowhere", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.ValidationFailed);
    }

    [Fact]
    public async Task Navigate_UnknownTailNumber_ReturnsVehicleNotFound()
    {
        var result = await _sut.Navigate("UAV-99", "target alpha", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.VehicleNotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task SetSpeed_OutOfRange_ReturnsValidationFailed(int speed)
    {
        var result = await _sut.SetSpeed("UAV-1", speed, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.ValidationFailed);
    }

    [Fact]
    public async Task SetSpeed_ValidRange_UpdatesSpeed()
    {
        var result = await _sut.SetSpeed("UAV-1", 200, CancellationToken.None);

        result.Success.Should().BeTrue();
        ((TelemetrySnapshot)result.Value!).SpeedKts.Should().Be(200);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public async Task SetAltitude_OutOfRange_ReturnsValidationFailed(int altitude)
    {
        var result = await _sut.SetAltitude("UAV-1", altitude, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.ValidationFailed);
    }

    [Fact]
    public async Task ReturnToLaunch_SetsReturningMode()
    {
        var result = await _sut.ReturnToLaunch("UAV-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        ((TelemetrySnapshot)result.Value!).Mode.Should().Be("ReturningToLaunch");
    }

    [Fact]
    public async Task PointPayload_KnownLocation_LocksPayload()
    {
        var result = await _sut.PointPayload("UAV-1", "target bravo", CancellationToken.None);

        result.Success.Should().BeTrue();
        ((TelemetrySnapshot)result.Value!).PayloadLockedOn.Should().Be("target bravo");
    }

    [Fact]
    public async Task ResetPayload_ClearsLock()
    {
        await _sut.PointPayload("UAV-1", "target alpha", CancellationToken.None);

        var result = await _sut.ResetPayload("UAV-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        ((TelemetrySnapshot)result.Value!).PayloadLockedOn.Should().BeNull();
    }

    [Fact]
    public async Task UploadWaypoints_StoresWaypointsAndReportsCount()
    {
        var waypoints = new List<Waypoint>
        {
            new(31.8, 34.6, 3000),
            new(31.9, 34.7, 3500),
        };

        var result = await _sut.UploadWaypoints("UAV-1", waypoints, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(2);

        var status = await _sut.GetMissionStatus("UAV-1", CancellationToken.None);
        ((MissionStatus)status.Value!).WaypointCount.Should().Be(2);
    }

    [Fact]
    public async Task MutatingOneVehicle_NeverAffectsAnother()
    {
        await _sut.SetSpeed("UAV-2", 300, CancellationToken.None);

        var uav1 = await _sut.GetTelemetry("UAV-1", CancellationToken.None);
        var uav3 = await _sut.GetTelemetry("UAV-3", CancellationToken.None);

        ((TelemetrySnapshot)uav1.Value!).SpeedKts.Should().Be(105);
        ((TelemetrySnapshot)uav3.Value!).SpeedKts.Should().Be(105);
    }

    [Fact]
    public async Task GetLinkStatus_KnownTailNumber_ReturnsDefaultStatus()
    {
        var result = await _sut.GetLinkStatus("UAV-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        var status = (GdtLinkStatus)result.Value!;
        status.LinkState.Should().Be("Connected");
        status.TrackingMode.Should().Be("Auto");
    }

    [Fact]
    public async Task GetLinkStatus_UnknownTailNumber_ReturnsNotFound()
    {
        var result = await _sut.GetLinkStatus("UAV-99", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.VehicleNotFound);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("manual")]
    [InlineData("Auto")]
    public async Task SetTrackingMode_ValidMode_Succeeds(string mode)
    {
        var result = await _sut.SetTrackingMode("UAV-1", mode, CancellationToken.None);

        result.Success.Should().BeTrue();
        ((GdtLinkStatus)result.Value!).TrackingMode.Should().Be(mode);
    }

    [Fact]
    public async Task SetTrackingMode_InvalidMode_ReturnsValidationFailed()
    {
        var result = await _sut.SetTrackingMode("UAV-1", "Bogus", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.ValidationFailed);
    }

    [Fact]
    public async Task SetTrackingMode_UnknownTailNumber_ReturnsNotFound()
    {
        var result = await _sut.SetTrackingMode("UAV-99", "Auto", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.VehicleNotFound);
    }
}

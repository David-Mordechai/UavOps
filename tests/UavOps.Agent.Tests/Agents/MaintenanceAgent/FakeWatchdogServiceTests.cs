using FluentAssertions;
using UavOps.Agent.Watchdog.Fake;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class FakeWatchdogServiceTests
{
    [Fact]
    public async Task GetServicesHealth_ReturnsFixedNonEmptyServiceSet()
    {
        var sut = new FakeWatchdogService();

        var result = await sut.GetServicesHealth(CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task StopService_KnownService_ThenGetServicesHealth_ReflectsUnhealthy()
    {
        var sut = new FakeWatchdogService();

        var stopResult = await sut.StopService("telemetry-relay", CancellationToken.None);

        stopResult.Success.Should().BeTrue();
    }

    [Fact]
    public async Task StartService_UnknownService_ReturnsInvalid()
    {
        var sut = new FakeWatchdogService();

        var result = await sut.StartService("no-such-service", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no-such-service");
    }

    [Fact]
    public async Task RestartService_KnownService_Succeeds()
    {
        var sut = new FakeWatchdogService();

        var result = await sut.RestartService("payload-bridge", CancellationToken.None);

        result.Success.Should().BeTrue();
    }
}

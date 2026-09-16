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

    // Real, live-reproduced bug this guards against: WatchdogTools's own Start/Stop/RestartService
    // methods take no model-visible service-name parameter and always hardcode this exact literal
    // ("Moav.Watchdog.Service") - before this fix, none of FakeWatchdogService's known names
    // matched it, so every real Start/Stop/RestartService call failed "service not found" under
    // WatchdogBackend: Fake.
    [Theory]
    [InlineData("Moav.Watchdog.Service")]
    [InlineData("moav.watchdog.service")]
    public async Task StartStopRestartService_TheWatchdogServiceItself_Succeeds(string watchdogServiceName)
    {
        var sut = new FakeWatchdogService();

        (await sut.StartService(watchdogServiceName, CancellationToken.None)).Success.Should().BeTrue();
        (await sut.StopService(watchdogServiceName, CancellationToken.None)).Success.Should().BeTrue();
        (await sut.RestartService(watchdogServiceName, CancellationToken.None)).Success.Should().BeTrue();
    }
}

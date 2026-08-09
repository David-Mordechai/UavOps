using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents.MaintenanceAgent;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class WatchdogServiceTests
{
    private static WatchdogService CreateSut(
        IWatchdogHealthStore? healthStore = null, IWindowsServiceController? serviceController = null, WatchdogOptions? options = null)
    {
        return new WatchdogService(
            healthStore ?? Substitute.For<IWatchdogHealthStore>(),
            serviceController ?? Substitute.For<IWindowsServiceController>(),
            options ?? new WatchdogOptions { ServiceNameMap = new Dictionary<string, string> { ["telemetry-relay"] = "TelemetryRelaySvc" } },
            NullLogger<WatchdogService>.Instance);
    }

    [Fact]
    public async Task GetServicesHealth_NoSnapshotYet_ReturnsUnknownAndStale()
    {
        var store = Substitute.For<IWatchdogHealthStore>();
        store.Current.Returns((WatchdogHealthSnapshot?)null);
        var sut = CreateSut(healthStore: store);

        var result = await sut.GetServicesHealth(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            overallStatus = "Unknown",
            services = new Dictionary<string, ServiceHealthEntry>(),
            polledAt = (DateTimeOffset?)null,
            stale = true
        });
    }

    [Fact]
    public async Task GetServicesHealth_WithSnapshot_ReturnsItsData()
    {
        var snapshot = new WatchdogHealthSnapshot(
            "Healthy",
            new Dictionary<string, ServiceHealthEntry> { ["telemetry-relay"] = new("Healthy", null) },
            DateTimeOffset.UtcNow,
            Stale: false);
        var store = Substitute.For<IWatchdogHealthStore>();
        store.Current.Returns(snapshot);
        var sut = CreateSut(healthStore: store);

        var result = await sut.GetServicesHealth(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            overallStatus = "Healthy",
            services = snapshot.Services,
            polledAt = (DateTimeOffset?)snapshot.PolledAt,
            stale = false
        });
    }

    [Fact]
    public async Task StartService_UnmappedName_ReturnsInvalid_AndNeverCallsController()
    {
        var controller = Substitute.For<IWindowsServiceController>();
        var sut = CreateSut(serviceController: controller);

        var result = await sut.StartService("no-such-service", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no-such-service");
        await controller.DidNotReceiveWithAnyArgs().Start(default!, default);
    }

    [Fact]
    public async Task StartService_MappedName_CallsControllerWithTheMappedWindowsServiceName()
    {
        var controller = Substitute.For<IWindowsServiceController>();
        var sut = CreateSut(serviceController: controller);

        var result = await sut.StartService("telemetry-relay", CancellationToken.None);

        result.Success.Should().BeTrue();
        await controller.Received(1).Start("TelemetryRelaySvc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopService_MappedName_CallsControllerWithTheMappedWindowsServiceName()
    {
        var controller = Substitute.For<IWindowsServiceController>();
        var sut = CreateSut(serviceController: controller);

        var result = await sut.StopService("telemetry-relay", CancellationToken.None);

        result.Success.Should().BeTrue();
        await controller.Received(1).Stop("TelemetryRelaySvc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestartService_MappedName_CallsControllerWithTheMappedWindowsServiceName()
    {
        var controller = Substitute.For<IWindowsServiceController>();
        var sut = CreateSut(serviceController: controller);

        var result = await sut.RestartService("telemetry-relay", CancellationToken.None);

        result.Success.Should().BeTrue();
        await controller.Received(1).Restart("TelemetryRelaySvc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnyMethod_UnderlyingException_ReturnsErrorInsteadOfThrowing()
    {
        var controller = Substitute.For<IWindowsServiceController>();
        controller.Start("TelemetryRelaySvc", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var sut = CreateSut(serviceController: controller);

        var result = await sut.StartService("telemetry-relay", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("boom");
    }
}

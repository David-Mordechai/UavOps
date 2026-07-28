using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Operations;
using UavOps.Agent.Operations.Remote;
using Xunit;

namespace UavOps.Agent.Tests.Operations;

public class RemoteOperationServiceTests
{
    private static (RemoteOperationService Service, IRemoteOperationBroker Broker) CreateSut()
    {
        var broker = Substitute.For<IRemoteOperationBroker>();
        var service = new RemoteOperationService(broker, NullLogger<RemoteOperationService>.Instance);
        return (service, broker);
    }

    [Fact]
    public async Task GetTelemetry_BrokerSucceeds_ReturnsOk()
    {
        var (service, broker) = CreateSut();
        var snapshot = new TelemetrySnapshot(31.8, 34.6, 120, 4000, "Orbiting", null);
        broker.SendAsync<TelemetrySnapshot>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(snapshot)));

        var result = await service.GetTelemetry("UAV-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(snapshot);
    }

    [Fact]
    public async Task GetTelemetry_BrokerFails_PropagatesRealErrorFaithfully()
    {
        // Unlike the old REST-controller-driven split interfaces (which flattened every broker
        // failure into a generic "ValidationFailed"), the unified OperationError flows through
        // unchanged — the caller sees exactly why it failed.
        var (service, broker) = CreateSut();
        broker.SendAsync<TelemetrySnapshot>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Fail(OperationError.NoClientConnected, "No fleet command client is connected.")));

        var result = await service.GetTelemetry("UAV-1", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.NoClientConnected);
        result.ErrorMessage.Should().Be("No fleet command client is connected.");
    }

    [Fact]
    public async Task Navigate_PassesTailNumberAndLocationToClientProxy()
    {
        // Proves the invoke lambda RemoteOperationService builds actually calls the right
        // typed proxy method with the right arguments — mocking only IRemoteOperationBroker's
        // return value (as the other tests here do) can't catch an args mix-up, since the lambda
        // is never run.
        var (service, broker) = CreateSut();
        var proxy = Substitute.For<IOperationClientProxy>();
        Func<IOperationClientProxy, string, Task>? capturedInvoke = null;

        broker.SendAsync<TelemetrySnapshot>(
                Arg.Do<Func<IOperationClientProxy, string, Task>>(invoke => capturedInvoke = invoke),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new TelemetrySnapshot(0, 0, 0, 0, "Transiting", null))));

        await service.Navigate("UAV-1", "target alpha", CancellationToken.None);

        capturedInvoke.Should().NotBeNull();
        _ = capturedInvoke!(proxy, "corr1");
        _ = proxy.Received(1).Navigate("corr1", "UAV-1", "target alpha");
    }

    [Fact]
    public async Task ListFleet_BrokerSucceeds_ReturnsValue()
    {
        var (service, broker) = CreateSut();
        var summaries = new List<UavSummary> { new("UAV-1", "Orbiting", 31.8, 34.6) };
        broker.SendAsync<List<UavSummary>>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(summaries)));

        var result = await service.ListFleet(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(summaries);
    }

    [Fact]
    public async Task ListFleet_BrokerFails_ReturnsFailure()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<List<UavSummary>>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Fail(OperationError.Timeout, "timed out")));

        var result = await service.ListFleet(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OperationError.Timeout);
    }

    [Fact]
    public async Task UploadWaypoints_BrokerSucceeds_ReturnsAcceptedCount()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<int>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(3)));

        var result = await service.UploadWaypoints("UAV-1", [new Waypoint(1, 2, 100)], CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(3);
    }

    [Fact]
    public async Task GetMissionStatus_BrokerSucceeds_ReturnsStatus()
    {
        var (service, broker) = CreateSut();
        var status = new MissionStatus("Orbiting", 2);
        broker.SendAsync<MissionStatus>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(status)));

        var result = await service.GetMissionStatus("UAV-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(status);
    }

    [Fact]
    public async Task GetLinkStatus_BrokerSucceeds_ReturnsStatus()
    {
        var (service, broker) = CreateSut();
        var status = new GdtLinkStatus("Connected", 92, "Auto");
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(status)));

        var result = await service.GetLinkStatus("UAV-1", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(status);
    }

    [Fact]
    public async Task SetTrackingMode_BrokerSucceeds_ReturnsStatus()
    {
        var (service, broker) = CreateSut();
        var status = new GdtLinkStatus("Connected", 92, "Manual");
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(status)));

        var result = await service.SetTrackingMode("UAV-1", "Manual", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(status);
    }

    [Fact]
    public async Task SetTrackingMode_BrokerFails_ReturnsFailureWithRealReason()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Fail(OperationError.Timeout, "Fleet command client did not respond in time.")));

        var result = await service.SetTrackingMode("UAV-1", "Manual", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Fleet command client did not respond in time.");
    }
}

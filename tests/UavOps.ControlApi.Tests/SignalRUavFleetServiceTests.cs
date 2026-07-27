using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Models;
using UavOps.ControlApi.Services;
using Xunit;

namespace UavOps.ControlApi.Tests;

public class SignalRUavFleetServiceTests
{
    private static (SignalRUavFleetService Service, IUavCommandBroker Broker) CreateSut()
    {
        var broker = Substitute.For<IUavCommandBroker>();
        var service = new SignalRUavFleetService(broker, NullLogger<SignalRUavFleetService>.Instance);
        return (service, broker);
    }

    [Fact]
    public void GetTelemetry_BrokerSucceeds_ReturnsOk()
    {
        var (service, broker) = CreateSut();
        var snapshot = new TelemetrySnapshot(31.8, 34.6, 120, 4000, "Orbiting", null);
        broker.SendAsync<TelemetrySnapshot>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<TelemetrySnapshot>.Ok(snapshot)));

        var result = service.GetTelemetry("UAV-1");

        result.Success.Should().BeTrue();
        result.Snapshot.Should().Be(snapshot);
    }

    [Fact]
    public void GetTelemetry_BrokerFails_ReturnsInvalidWithBrokerMessage()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<TelemetrySnapshot>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<TelemetrySnapshot>.Fail(BrokerError.NoClientConnected, "No fleet command client is connected.")));

        var result = service.GetTelemetry("UAV-1");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(UavCommandError.ValidationFailed);
        result.ErrorMessage.Should().Be("No fleet command client is connected.");
    }

    [Fact]
    public void Navigate_PassesTailNumberAndLocationToClientProxy()
    {
        // Proves the invoke lambda SignalRUavFleetService builds actually calls the right typed
        // proxy method with the right arguments — mocking only IUavCommandBroker's return value
        // (as the other tests here do) can't catch an args mix-up, since the lambda is never run.
        var (service, broker) = CreateSut();
        var proxy = Substitute.For<IUavCommandClientProxy>();
        Func<IUavCommandClientProxy, string, Task>? capturedInvoke = null;

        broker.SendAsync<TelemetrySnapshot>(
                Arg.Do<Func<IUavCommandClientProxy, string, Task>>(invoke => capturedInvoke = invoke),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<TelemetrySnapshot>.Ok(new TelemetrySnapshot(0, 0, 0, 0, "Transiting", null))));

        service.Navigate("UAV-1", "target alpha");

        capturedInvoke.Should().NotBeNull();
        capturedInvoke!(proxy, "corr1");
        proxy.Received(1).Navigate("corr1", "UAV-1", "target alpha");
    }

    [Fact]
    public void ListFleet_BrokerSucceeds_ReturnsValue()
    {
        var (service, broker) = CreateSut();
        var summaries = new List<UavSummary> { new("UAV-1", "Orbiting", 31.8, 34.6) };
        broker.SendAsync<List<UavSummary>>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<List<UavSummary>>.Ok(summaries)));

        var result = service.ListFleet();

        result.Should().BeEquivalentTo(summaries);
    }

    [Fact]
    public void ListFleet_BrokerFails_ReturnsEmptyCollection()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<List<UavSummary>>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<List<UavSummary>>.Fail(BrokerError.Timeout, "timed out")));

        var result = service.ListFleet();

        result.Should().BeEmpty();
    }

    [Fact]
    public void UploadWaypoints_BrokerSucceeds_ReturnsFoundAndAcceptedCount()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<int>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<int>.Ok(3)));

        var (found, accepted) = service.UploadWaypoints("UAV-1", [new Waypoint(1, 2, 100)]);

        found.Should().BeTrue();
        accepted.Should().Be(3);
    }

    [Fact]
    public void UploadWaypoints_BrokerFails_ReturnsNotFound()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<int>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<int>.Fail(BrokerError.NoClientConnected, "no client")));

        var (found, accepted) = service.UploadWaypoints("UAV-1", []);

        found.Should().BeFalse();
        accepted.Should().Be(0);
    }

    [Fact]
    public void GetMissionStatus_BrokerSucceeds_ReturnsFoundAndStatus()
    {
        var (service, broker) = CreateSut();
        var status = new MissionStatus("Orbiting", 2);
        broker.SendAsync<MissionStatus>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<MissionStatus>.Ok(status)));

        var (found, result) = service.GetMissionStatus("UAV-1");

        found.Should().BeTrue();
        result.Should().Be(status);
    }

    [Fact]
    public void GetMissionStatus_BrokerFails_ReturnsNotFound()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<MissionStatus>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<MissionStatus>.Fail(BrokerError.Timeout, "timed out")));

        var (found, result) = service.GetMissionStatus("UAV-1");

        found.Should().BeFalse();
        result.Should().BeNull();
    }
}

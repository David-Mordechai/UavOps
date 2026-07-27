using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Models;
using UavOps.ControlApi.Services;
using Xunit;

namespace UavOps.ControlApi.Tests;

public class SignalRGdtServiceTests
{
    private static (SignalRGdtService Service, IUavCommandBroker Broker) CreateSut()
    {
        var broker = Substitute.For<IUavCommandBroker>();
        var service = new SignalRGdtService(broker, NullLogger<SignalRGdtService>.Instance);
        return (service, broker);
    }

    [Fact]
    public void GetLinkStatus_BrokerSucceeds_ReturnsFoundAndStatus()
    {
        var (service, broker) = CreateSut();
        var status = new GdtLinkStatus("Connected", 92, "Auto");
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<GdtLinkStatus>.Ok(status)));

        var (found, result) = service.GetLinkStatus("UAV-1");

        found.Should().BeTrue();
        result.Should().Be(status);
    }

    [Fact]
    public void GetLinkStatus_BrokerFails_ReturnsNotFound()
    {
        var (service, broker) = CreateSut();
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<GdtLinkStatus>.Fail(BrokerError.NoClientConnected, "no client")));

        var (found, result) = service.GetLinkStatus("UAV-1");

        found.Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void SetTrackingMode_BrokerSucceeds_ReturnsFoundStatusNoError()
    {
        var (service, broker) = CreateSut();
        var status = new GdtLinkStatus("Connected", 92, "Manual");
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<GdtLinkStatus>.Ok(status)));

        var (found, result, error) = service.SetTrackingMode("UAV-1", "Manual");

        found.Should().BeTrue();
        result.Should().Be(status);
        error.Should().BeNull();
    }

    [Fact]
    public void SetTrackingMode_BrokerFails_ReturnsFoundTrueWithErrorMessage()
    {
        // Found=true (not a 404) so the real broker error message reaches the HTTP caller as a
        // 400 — see SignalRGdtService's error-mapping note.
        var (service, broker) = CreateSut();
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IUavCommandClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BrokerResult<GdtLinkStatus>.Fail(BrokerError.Timeout, "Fleet command client did not respond in time.")));

        var (found, result, error) = service.SetTrackingMode("UAV-1", "Manual");

        found.Should().BeTrue();
        result.Should().BeNull();
        error.Should().Be("Fleet command client did not respond in time.");
    }
}

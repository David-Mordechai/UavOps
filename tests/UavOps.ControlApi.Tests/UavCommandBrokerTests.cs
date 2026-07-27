using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Options;
using UavOps.ControlApi.Services;
using Xunit;

namespace UavOps.ControlApi.Tests;

public class UavCommandBrokerTests
{
    private static (UavCommandBroker Broker, IUavCommandClientProxy Proxy) CreateSut(TimeSpan? timeout = null)
    {
        var proxy = Substitute.For<IUavCommandClientProxy>();
        var clients = Substitute.For<IHubClients<IUavCommandClientProxy>>();
        clients.Client(Arg.Any<string>()).Returns(proxy);

        var hub = Substitute.For<IHubContext<UavCommandHub, IUavCommandClientProxy>>();
        hub.Clients.Returns(clients);

        var broker = new UavCommandBroker(
            hub, new FleetBridgeOptions(), NullLogger<UavCommandBroker>.Instance, timeout ?? TimeSpan.FromSeconds(30));

        return (broker, proxy);
    }

    [Fact]
    public async Task SendAsync_NoClientConnected_ReturnsFailureImmediately_NoTimeoutWait()
    {
        var (broker, _) = CreateSut(TimeSpan.FromSeconds(30));

        var sw = Stopwatch.StartNew();
        var result = await broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "target alpha"), CancellationToken.None);
        sw.Stop();

        result.Success.Should().BeFalse();
        result.Error.Should().Be(BrokerError.NoClientConnected);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a no-client failure must not wait out the timeout");
    }

    [Fact]
    public async Task SendAsync_ClientRespondsSuccess_ReturnsDeserializedValue()
    {
        var (broker, proxy) = CreateSut();
        broker.RegisterConnection("conn1");

        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                _ = broker.Complete(callInfo.ArgAt<string>(0), true, null, "\"navigated\"");
                return Task.CompletedTask;
            });

        var result = await broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "target alpha"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().Be("navigated");
    }

    [Fact]
    public async Task SendAsync_ClientReportsError_ReturnsClientReportedError()
    {
        var (broker, proxy) = CreateSut();
        broker.RegisterConnection("conn1");

        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                _ = broker.Complete(callInfo.ArgAt<string>(0), false, "hardware fault", null);
                return Task.CompletedTask;
            });

        var result = await broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "target alpha"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(BrokerError.ClientReportedError);
        result.ErrorMessage.Should().Be("hardware fault");
    }

    [Fact]
    public async Task SendAsync_NoResponse_TimesOutAndReturnsTimeout()
    {
        var (broker, proxy) = CreateSut(TimeSpan.FromMilliseconds(100));
        broker.RegisterConnection("conn1");
        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "target alpha"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(BrokerError.Timeout);
    }

    [Fact]
    public async Task SendAsync_TwoConcurrentCommands_EachResolveIndependently()
    {
        var (broker, proxy) = CreateSut();
        broker.RegisterConnection("conn1");

        string? navigateCorrelationId = null;
        string? setSpeedCorrelationId = null;
        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => { navigateCorrelationId = callInfo.ArgAt<string>(0); return Task.CompletedTask; });
        proxy.SetSpeed(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(callInfo => { setSpeedCorrelationId = callInfo.ArgAt<string>(0); return Task.CompletedTask; });

        var navigateTask = broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "target alpha"), CancellationToken.None);
        var setSpeedTask = broker.SendAsync<string>((p, cid) => p.SetSpeed(cid, "UAV-2", 200), CancellationToken.None);

        navigateCorrelationId.Should().NotBeNullOrEmpty();
        setSpeedCorrelationId.Should().NotBeNullOrEmpty();
        navigateCorrelationId.Should().NotBe(setSpeedCorrelationId);

        // Resolve out of order (SetSpeed first) to prove each result routes to the right caller.
        await broker.Complete(setSpeedCorrelationId!, true, null, "\"speed set\"");
        await broker.Complete(navigateCorrelationId!, true, null, "\"navigated\"");

        (await navigateTask).Value.Should().Be("navigated");
        (await setSpeedTask).Value.Should().Be("speed set");
    }

    [Fact]
    public async Task RegisterConnection_SecondClientConnects_LastWriterWinsRouting()
    {
        var proxy = Substitute.For<IUavCommandClientProxy>();
        var clients = Substitute.For<IHubClients<IUavCommandClientProxy>>();
        clients.Client(Arg.Any<string>()).Returns(proxy);
        var hub = Substitute.For<IHubContext<UavCommandHub, IUavCommandClientProxy>>();
        hub.Clients.Returns(clients);
        var broker = new UavCommandBroker(hub, new FleetBridgeOptions(), NullLogger<UavCommandBroker>.Instance, TimeSpan.FromSeconds(30));

        broker.RegisterConnection("conn1");
        broker.RegisterConnection("conn2");

        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                _ = broker.Complete(callInfo.ArgAt<string>(0), true, null, "\"ok\"");
                return Task.CompletedTask;
            });

        await broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "x"), CancellationToken.None);

        clients.Received(1).Client("conn2");
        clients.DidNotReceive().Client("conn1");
    }

    [Fact]
    public async Task UnregisterConnection_MatchingCurrentConnection_FailsPendingCommandsImmediately()
    {
        var (broker, proxy) = CreateSut(TimeSpan.FromSeconds(30));
        broker.RegisterConnection("conn1");
        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask); // never replies

        var task = broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "x"), CancellationToken.None);

        var sw = Stopwatch.StartNew();
        broker.UnregisterConnection("conn1");
        var result = await task;
        sw.Stop();

        result.Success.Should().BeFalse();
        result.Error.Should().Be(BrokerError.NoClientConnected);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "disconnect should fail in-flight commands immediately, not wait out the timeout");
    }

    [Fact]
    public async Task UnregisterConnection_StaleConnection_DoesNotClearNewerConnection()
    {
        var (broker, proxy) = CreateSut();
        broker.RegisterConnection("conn1");
        broker.RegisterConnection("conn2"); // replaces conn1
        broker.UnregisterConnection("conn1"); // stale disconnect for the replaced connection

        proxy.Navigate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                _ = broker.Complete(callInfo.ArgAt<string>(0), true, null, "\"ok\"");
                return Task.CompletedTask;
            });

        var result = await broker.SendAsync<string>((p, cid) => p.Navigate(cid, "UAV-1", "x"), CancellationToken.None);

        result.Success.Should().BeTrue("conn2 is still tracked as connected — the stale conn1 disconnect must not clear it");
    }
}

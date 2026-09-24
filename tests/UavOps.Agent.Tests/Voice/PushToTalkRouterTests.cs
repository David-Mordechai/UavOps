using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Voice;
using Xunit;

namespace UavOps.Agent.Tests.Voice;

public class PushToTalkRouterTests
{
    private const string FleetClient = "fleet-conn";

    /// <summary>Every SetMicActive send, as "connectionId:true/false", in order.</summary>
    private readonly List<string> _sends = [];
    private readonly PushToTalkRouter _sut;

    public PushToTalkRouterTests()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Client(Arg.Any<string>()).Returns(callInfo =>
        {
            var connectionId = callInfo.Arg<string>();
            var proxy = Substitute.For<ISingleClientProxy>();
            proxy
                .SendCoreAsync(PushToTalkRouter.MicActiveEvent, Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
                .Returns(sendCall =>
                {
                    _sends.Add($"{connectionId}:{sendCall.ArgAt<object?[]>(1)[0]}".ToLowerInvariant());
                    return Task.CompletedTask;
                });
            return proxy;
        });

        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);
        _sut = new PushToTalkRouter(hub, NullLogger<PushToTalkRouter>.Instance);
    }

    [Fact]
    public async Task Press_WithNoChatTab_ReturnsFalseAndSendsNothing()
    {
        (await _sut.SetPushToTalkAsync(FleetClient, pressed: true)).Should().BeFalse();
        _sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Press_GoesOnlyToTabWithNewestReportedActivity_NotLastReporter()
    {
        _sut.ReportActivity("tab-new", lastActivityUnixMs: 2000);
        _sut.ReportActivity("tab-old", lastActivityUnixMs: 1000); // reported last, but used earlier

        (await _sut.SetPushToTalkAsync(FleetClient, pressed: true)).Should().BeTrue();

        _sends.Should().Equal("tab-new:true");
    }

    [Fact]
    public async Task Release_GoesToTabThatGotThePress_EvenIfAnotherTabIsNowNewer()
    {
        _sut.ReportActivity("tab-a", 1000);
        await _sut.SetPushToTalkAsync(FleetClient, pressed: true);
        _sut.ReportActivity("tab-b", 2000);

        (await _sut.SetPushToTalkAsync(FleetClient, pressed: false)).Should().BeTrue();

        _sends.Should().Equal("tab-a:true", "tab-a:false");
    }

    [Fact]
    public async Task Release_WithoutPress_ReturnsFalseAndSendsNothing()
    {
        _sut.ReportActivity("tab-a", 1000);

        (await _sut.SetPushToTalkAsync(FleetClient, pressed: false)).Should().BeFalse();
        _sends.Should().BeEmpty();
    }

    [Fact]
    public async Task FleetClientDisconnectingWhileHeld_ReleasesTheMic()
    {
        _sut.ReportActivity("tab-a", 1000);
        await _sut.SetPushToTalkAsync(FleetClient, pressed: true);

        await _sut.ConnectionClosedAsync(FleetClient);

        _sends.Should().Equal("tab-a:true", "tab-a:false");
    }

    [Fact]
    public async Task FleetClientDisconnectingAfterRelease_SendsNothingMore()
    {
        _sut.ReportActivity("tab-a", 1000);
        await _sut.SetPushToTalkAsync(FleetClient, pressed: true);
        await _sut.SetPushToTalkAsync(FleetClient, pressed: false);

        await _sut.ConnectionClosedAsync(FleetClient);

        _sends.Should().Equal("tab-a:true", "tab-a:false");
    }

    [Fact]
    public async Task ClosedChatTab_IsNoLongerATarget()
    {
        _sut.ReportActivity("tab-a", 1000);
        _sut.ReportActivity("tab-b", 2000);

        await _sut.ConnectionClosedAsync("tab-b");
        await _sut.SetPushToTalkAsync(FleetClient, pressed: true);

        _sends.Should().Equal("tab-a:true");
    }
}

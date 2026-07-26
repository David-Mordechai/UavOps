using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class ConfirmationGateTests
{
    /// <summary>
    /// Builds a ConfirmationGate whose outbound SendAsync call is captured — the confirmationId
    /// is generated internally and never returned to the caller, so the only way a test can
    /// resolve it is to intercept the SignalR send and react to it, exactly like the real chat
    /// client does over the wire.
    /// </summary>
    private static ConfirmationGate CreateSut(TimeSpan timeout, Action<string> onConfirmationRequested)
    {
        var clientProxy = Substitute.For<IClientProxy>();
        clientProxy
            .SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<object?[]>(1);
                onConfirmationRequested((string)args[0]!);
                return Task.CompletedTask;
            });

        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(clientProxy);

        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);

        var config = new ConfigurationBuilder().Build();
        return new ConfirmationGate(hub, config, NullLogger<ConfirmationGate>.Instance, timeout);
    }

    [Fact]
    public async Task RequireConfirmationAsync_ApprovedBeforeTimeout_ReturnsTrue()
    {
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), confirmationId => sut!.Resolve(confirmationId, true));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeTrue();
    }

    [Fact]
    public async Task RequireConfirmationAsync_DeclinedBeforeTimeout_ReturnsFalse()
    {
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), confirmationId => sut!.Resolve(confirmationId, false));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeFalse();
    }

    [Fact]
    public async Task RequireConfirmationAsync_NoResponse_TimesOutAndReturnsFalse()
    {
        // Nobody calls Resolve — the short injected timeout is what must fire, deterministically
        // and quickly, rather than the real 60s default.
        var sut = CreateSut(TimeSpan.FromMilliseconds(50), _ => { });

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeFalse();
    }

    [Fact]
    public void Resolve_UnknownConfirmationId_DoesNothing()
    {
        var sut = CreateSut(TimeSpan.FromSeconds(30), _ => { });

        var act = () => sut.Resolve("never-requested", true);

        act.Should().NotThrow();
    }
}

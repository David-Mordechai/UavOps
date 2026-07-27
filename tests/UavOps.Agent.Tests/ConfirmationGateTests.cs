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
    /// Builds a ConfirmationGate whose outbound "ReceiveChatMessage" sends are captured — the
    /// gate has no confirmationId in its public surface any more (resolution happens by parsing
    /// the operator's next chat reply, exactly like the real chat client), so tests observe the
    /// prompt the same way the UI does and then feed a reply back through
    /// <see cref="ConfirmationGate.TryHandleChatReplyAsync"/>.
    /// </summary>
    private static ConfirmationGate CreateSut(TimeSpan timeout, Action<string>? onPrompt = null)
    {
        // onPrompt only fires for the *first* outbound message (the prompt itself) — later sends
        // on the same confirmation (a re-prompt after an unrecognized reply, or the final
        // approved/declined notice) must not re-trigger it, or a test reply that itself doesn't
        // parse as yes/no would recurse forever against its own re-prompt.
        var promptSent = false;
        var clientProxy = Substitute.For<IClientProxy>();
        clientProxy
            .SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (!promptSent)
                {
                    promptSent = true;
                    var args = callInfo.ArgAt<object?[]>(1);
                    onPrompt?.Invoke((string)args[1]!); // args: agentName, text, duration, correlationId
                }
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
    public async Task RequireConfirmationAsync_ApprovedByChatReply_ReturnsTrue()
    {
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), promptText => sut!.TryHandleChatReplyAsync("yes", CancellationToken.None));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", "Change speed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeTrue();
    }

    [Fact]
    public async Task RequireConfirmationAsync_DeclinedByChatReply_ReturnsFalse()
    {
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), promptText => sut!.TryHandleChatReplyAsync("no", CancellationToken.None));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", "Change speed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeFalse();
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Yes!")]
    [InlineData("sure")]
    [InlineData("go ahead")]
    public async Task RequireConfirmationAsync_RecognizesVariousAffirmativeReplies(string reply)
    {
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), promptText => sut!.TryHandleChatReplyAsync(reply, CancellationToken.None));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", "Change speed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeTrue();
    }

    [Fact]
    public async Task RequireConfirmationAsync_UnrecognizedReply_KeepsWaitingUntilTimeout()
    {
        // "maybe" doesn't parse as yes/no, so the gate should re-prompt and keep waiting rather
        // than guessing — the short injected timeout is what must eventually resolve it to false.
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromMilliseconds(50), promptText => sut!.TryHandleChatReplyAsync("maybe", CancellationToken.None));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", "Change speed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeFalse();
    }

    [Fact]
    public async Task RequireConfirmationAsync_NoReply_TimesOutAndReturnsFalse()
    {
        var sut = CreateSut(TimeSpan.FromMilliseconds(50));

        var approved = await sut.RequireConfirmationAsync("corr1", "TestAgent", "SetSpeed", "Change speed", new { speedKts = 100 }, CancellationToken.None);

        approved.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleChatReplyAsync_NoPendingConfirmation_ReturnsFalse()
    {
        var sut = CreateSut(TimeSpan.FromSeconds(30));

        var handled = await sut.TryHandleChatReplyAsync("yes", CancellationToken.None);

        handled.Should().BeFalse();
    }

    [Fact]
    public async Task RequireConfirmationAsync_PromptText_IsHumanReadable()
    {
        // The operator approving/declining a UAV action shouldn't need to know operationIds or
        // read raw JSON — the prompt should read like a sentence built from the tool's
        // human-authored Description and plain "name: value" arguments.
        string? promptText = null;
        ConfirmationGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), text =>
        {
            promptText = text;
            _ = sut!.TryHandleChatReplyAsync("yes", CancellationToken.None);
        });

        await sut.RequireConfirmationAsync(
            "corr1", "GdtControlAgent", "SetAntennaTrackingMode",
            "Set the ground data terminal antenna's tracking mode for a UAV.",
            new { tailNumber = "UAV-1", mode = "Manual" },
            CancellationToken.None);

        promptText.Should().NotBeNull();
        promptText.Should().Contain("Set the ground data terminal antenna's tracking mode for a UAV");
        promptText.Should().Contain("tailNumber: UAV-1");
        promptText.Should().Contain("mode: Manual");
        promptText.Should().NotContain("SetAntennaTrackingMode");
        promptText.Should().NotContain("{"); // not raw JSON — quotes are fine, used stylistically around yes/no
    }
}

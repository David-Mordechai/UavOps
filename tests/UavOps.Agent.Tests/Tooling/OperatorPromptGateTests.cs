using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Tooling;

public class OperatorPromptGateTests
{
    /// <summary>
    /// Builds an OperatorPromptGate whose outbound "ReceiveChatMessage" sends are captured —
    /// mirrors ConfirmationGateTests's CreateSut, since resolution here also happens by parsing
    /// the operator's next chat reply rather than through any other public API.
    /// </summary>
    private static OperatorPromptGate CreateSut(TimeSpan timeout, Action<string>? onPrompt = null)
    {
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

        return new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, timeout);
    }

    [Fact]
    public async Task RequestChoiceAsync_ExactNameReply_ReturnsThatChoice()
    {
        OperatorPromptGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), _ => sut!.TryHandleChatReplyAsync("lesson2.ps1", CancellationToken.None));

        var choice = await sut.RequestChoiceAsync("corr1", "SimulatorInfrastructureAgent", "Which lesson?",
            ["lesson1.ps1", "lesson2.ps1"], CancellationToken.None);

        choice.Should().Be("lesson2.ps1");
    }

    [Fact]
    public async Task RequestChoiceAsync_CaseInsensitiveReply_Matches()
    {
        OperatorPromptGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), _ => sut!.TryHandleChatReplyAsync("LESSON1.PS1", CancellationToken.None));

        var choice = await sut.RequestChoiceAsync("corr1", "SimulatorInfrastructureAgent", "Which lesson?",
            ["lesson1.ps1", "lesson2.ps1"], CancellationToken.None);

        choice.Should().Be("lesson1.ps1");
    }

    [Fact]
    public async Task RequestChoiceAsync_NumericIndexReply_ReturnsThatChoice()
    {
        OperatorPromptGate? sut = null;
        sut = CreateSut(TimeSpan.FromSeconds(30), _ => sut!.TryHandleChatReplyAsync("2", CancellationToken.None));

        var choice = await sut.RequestChoiceAsync("corr1", "SimulatorInfrastructureAgent", "Which lesson?",
            ["lesson1.ps1", "lesson2.ps1"], CancellationToken.None);

        choice.Should().Be("lesson2.ps1");
    }

    [Fact]
    public async Task RequestChoiceAsync_UnrecognizedReply_KeepsWaitingUntilTimeout()
    {
        OperatorPromptGate? sut = null;
        sut = CreateSut(TimeSpan.FromMilliseconds(50), _ => sut!.TryHandleChatReplyAsync("not a lesson", CancellationToken.None));

        var choice = await sut.RequestChoiceAsync("corr1", "SimulatorInfrastructureAgent", "Which lesson?",
            ["lesson1.ps1", "lesson2.ps1"], CancellationToken.None);

        choice.Should().BeNull();
    }

    [Fact]
    public async Task RequestChoiceAsync_NoReply_TimesOutAndReturnsNull()
    {
        var sut = CreateSut(TimeSpan.FromMilliseconds(50));

        var choice = await sut.RequestChoiceAsync("corr1", "SimulatorInfrastructureAgent", "Which lesson?",
            ["lesson1.ps1", "lesson2.ps1"], CancellationToken.None);

        choice.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleChatReplyAsync_NoPendingPrompt_ReturnsFalse()
    {
        var sut = CreateSut(TimeSpan.FromSeconds(30));

        var handled = await sut.TryHandleChatReplyAsync("lesson1.ps1", CancellationToken.None);

        handled.Should().BeFalse();
    }
}

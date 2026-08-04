using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class AskOperatorChoiceToolTests
{
    /// <summary>
    /// Builds a real AskOperatorChoiceTool wired to a hub mock that reacts to the operator prompt
    /// exactly like ChatHub does — mirrors OperationToolTests's CreateSutWithConfirmation, since
    /// this tool needs to exercise InvokeCoreAsync itself, not just the JsonSchema it builds.
    /// </summary>
    private static UavOps.Agent.Agents.SimulatorAgent.AskOperatorChoiceTool CreateSut(AgentToolConfig config, string? chatReply, string operatorText = "")
    {
        // Only forward the reply on the *first* outbound message (the prompt itself) — later
        // sends on the same prompt (e.g. a re-prompt after an unrecognized reply) must not
        // re-trigger it, or a reply that itself doesn't match a choice would recurse forever
        // against its own re-prompt (mirrors ConfirmationGateTests's CreateSut).
        var replySent = false;
        OperatorPromptGate? promptGate = null;
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (chatReply is not null && !replySent)
                {
                    replySent = true;
                    _ = promptGate!.TryHandleChatReplyAsync(chatReply, CancellationToken.None);
                }
                return Task.CompletedTask;
            });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);

        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        promptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, TimeSpan.FromSeconds(30));

        return new UavOps.Agent.Agents.SimulatorAgent.AskOperatorChoiceTool(config, promptGate, toolLogger, "SimulatorInfrastructureAgent", "corr1", operatorText);
    }

    private static AgentToolConfig Config() => new()
    {
        Kind = "OperatorPrompt",
        Operation = "AskOperatorWhichLesson",
        Description = "Which lesson would you like to run?",
        Parameters = new Dictionary<string, string> { ["lessons"] = "the discovered lesson names" }
    };

    [Fact]
    public void JsonSchema_DeclaresLessonsArrayParameter()
    {
        var sut = CreateSut(Config(), chatReply: null);

        var lessonsSchema = sut.JsonSchema.GetProperty("properties").GetProperty("lessons");

        lessonsSchema.GetProperty("type").GetString().Should().Be("array");
        lessonsSchema.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        lessonsSchema.GetProperty("description").GetString().Should().Be("the discovered lesson names");
    }

    [Fact]
    public async Task InvokeCoreAsync_OperatorChoosesFromList_ReturnsChoice()
    {
        var sut = CreateSut(Config(), chatReply: "lesson2.ps1");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = new[] { "lesson1.ps1", "lesson2.ps1" } }),
            CancellationToken.None);

        result!.ToString().Should().Be("lesson2.ps1");
    }

    [Fact]
    public async Task InvokeCoreAsync_NoValidReply_ReturnsNotAnswered()
    {
        var sut = CreateSut(Config(), chatReply: "not in the list");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = new[] { "lesson1.ps1", "lesson2.ps1" } }),
            CancellationToken.None);

        result!.ToString().Should().Contain("Not answered");
    }

    [Fact]
    public async Task InvokeCoreAsync_NoLessonsOffered_DoesNotPromptOperator()
    {
        var sut = CreateSut(Config(), chatReply: "lesson1.ps1");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = Array.Empty<string>() }),
            CancellationToken.None);

        result!.ToString().Should().Contain("No choices were offered");
    }

    [Fact]
    public async Task InvokeCoreAsync_OperatorTextNamesExactlyOneLesson_AutoResolvesWithoutPrompting()
    {
        // chatReply is null and would throw/no-op if the gate were actually invoked — proving
        // the prompt never went out.
        var sut = CreateSut(Config(), chatReply: null, operatorText: "run simulator with lesson restart-MultiUav.ps1 now");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = new[] { "restart-MultiUav.ps1", "stop-all.ps1" } }),
            CancellationToken.None);

        result!.ToString().Should().Be("restart-MultiUav.ps1");
    }

    [Fact]
    public async Task InvokeCoreAsync_OperatorTextNamesLessonWithoutExtension_AutoResolves()
    {
        var sut = CreateSut(Config(), chatReply: null, operatorText: "please run restart-MultiUav for me");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = new[] { "restart-MultiUav.ps1", "stop-all.ps1" } }),
            CancellationToken.None);

        result!.ToString().Should().Be("restart-MultiUav.ps1");
    }

    [Fact]
    public async Task InvokeCoreAsync_OperatorTextMatchesMultipleLessons_FallsBackToPrompting()
    {
        var sut = CreateSut(Config(), chatReply: "stop-all.ps1",
            operatorText: "not sure, maybe restart-MultiUav.ps1 or stop-all.ps1");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = new[] { "restart-MultiUav.ps1", "stop-all.ps1" } }),
            CancellationToken.None);

        result!.ToString().Should().Be("stop-all.ps1");
    }

    [Fact]
    public async Task InvokeCoreAsync_OperatorTextNamesNoLesson_FallsBackToPrompting()
    {
        var sut = CreateSut(Config(), chatReply: "lesson2.ps1", operatorText: "start the simulator please");

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["lessons"] = new[] { "lesson1.ps1", "lesson2.ps1" } }),
            CancellationToken.None);

        result!.ToString().Should().Be("lesson2.ps1");
    }
}

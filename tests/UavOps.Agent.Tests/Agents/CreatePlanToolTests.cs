using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents;

public class CreatePlanToolTests
{
    private sealed class FakeDelegateTool(string name) : AIFunction
    {
        public List<string> InvokedInstructions { get; } = [];

        public override string Name => name;
        public override string Description => $"Handles {name}.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            arguments.TryGetValue("instruction", out var raw);
            var instruction = raw?.ToString() ?? "";
            InvokedInstructions.Add(instruction);
            return ValueTask.FromResult<object?>($"{name} did: {instruction}");
        }
    }

    private static (CreatePlanTool Tool, FakeDelegateTool MoavAgent, FakeDelegateTool SimulatorAgent) CreateSut()
    {
        var moavAgent = new FakeDelegateTool("MoavAgent");
        var simulatorAgent = new FakeDelegateTool("SimulatorAgent");
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var clients = Substitute.For<IHubClients>();
        hub.Clients.Returns(clients);
        clients.All.Returns(Substitute.For<IClientProxy>());
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);

        var tool = new CreatePlanTool(
            new Dictionary<string, AIFunction> { ["MoavAgent"] = moavAgent, ["SimulatorAgent"] = simulatorAgent },
            toolLogger, "BrainAgent", "corr1");

        return (tool, moavAgent, simulatorAgent);
    }

    private static AIFunctionArguments StepsArgs(params (string Agent, string Instruction)[] steps)
    {
        var stepsJson = "[" + string.Join(",", steps.Select(s =>
            $"{{\"agent\":{JsonSerializer.Serialize(s.Agent)},\"instruction\":{JsonSerializer.Serialize(s.Instruction)}}}")) + "]";
        var doc = JsonDocument.Parse($"{{\"steps\":{stepsJson}}}");
        return new AIFunctionArguments(new Dictionary<string, object?> { ["steps"] = doc.RootElement.GetProperty("steps") });
    }

    [Fact]
    public async Task InvokeCoreAsync_EmptySteps_NoDelegateInvoked()
    {
        var (tool, moavAgent, simulatorAgent) = CreateSut();

        var result = await tool.InvokeAsync(StepsArgs(), CancellationToken.None);

        moavAgent.InvokedInstructions.Should().BeEmpty();
        simulatorAgent.InvokedInstructions.Should().BeEmpty();
        result!.ToString().Should().Contain("No delegation needed");
    }

    [Fact]
    public async Task InvokeCoreAsync_OneStep_InvokesNamedAgentWithInstruction()
    {
        var (tool, moavAgent, _) = CreateSut();

        var result = await tool.InvokeAsync(StepsArgs(("MoavAgent", "fly UAV-1 to alpha")), CancellationToken.None);

        moavAgent.InvokedInstructions.Should().ContainSingle().Which.Should().Be("fly UAV-1 to alpha");
        result!.ToString().Should().Contain("MoavAgent").And.Contain("fly UAV-1 to alpha").And.Contain("did: fly UAV-1 to alpha");
    }

    [Fact]
    public async Task InvokeCoreAsync_MultipleSteps_InvokesEachInOrder()
    {
        var (tool, moavAgent, simulatorAgent) = CreateSut();

        var result = await tool.InvokeAsync(
            StepsArgs(("MoavAgent", "fly UAV-1 to alpha"), ("SimulatorAgent", "start the simulator")), CancellationToken.None);

        moavAgent.InvokedInstructions.Should().ContainSingle().Which.Should().Be("fly UAV-1 to alpha");
        simulatorAgent.InvokedInstructions.Should().ContainSingle().Which.Should().Be("start the simulator");
        result!.ToString().Should().Contain("MoavAgent").And.Contain("SimulatorAgent");
    }

    [Fact]
    public async Task InvokeCoreAsync_StepNamesUnknownAgent_NotExecuted_OtherStepsStillRun()
    {
        var (tool, moavAgent, _) = CreateSut();

        var result = await tool.InvokeAsync(
            StepsArgs(("NotARealAgent", "do something"), ("MoavAgent", "fly UAV-1 to alpha")), CancellationToken.None);

        moavAgent.InvokedInstructions.Should().ContainSingle();
        result!.ToString().Should().Contain("NOT EXECUTED").And.Contain("unknown agent");
    }

    [Fact]
    public void JsonSchema_ListsAvailableAgentNames()
    {
        var (tool, _, _) = CreateSut();

        var schemaText = tool.JsonSchema.GetRawText();

        schemaText.Should().Contain("MoavAgent").And.Contain("SimulatorAgent");
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents.MoavAgent;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

public class FleetWideActionConfirmationToolTests
{
    private const string DelegateName = "MoavAgent";
    private const string CorrelationId = "corr1";

    private sealed class FakeInnerTool : AIFunction
    {
        public List<string> InvokedInstructions { get; } = [];

        public override string Name => DelegateName;
        public override string Description => "Coordinates fleet operations.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            arguments.TryGetValue("instruction", out var raw);
            InvokedInstructions.Add(raw?.ToString() ?? "");
            return ValueTask.FromResult<object?>("ok");
        }
    }

    private static List<UavSummary> ThreeUavFleet() =>
    [
        new UavSummary("UAV-1", "Orbiting", 0, 0),
        new UavSummary("UAV-2", "Orbiting", 0, 0),
        new UavSummary("UAV-3", "Orbiting", 0, 0)
    ];

    private static (FleetWideActionConfirmationTool Tool, FakeInnerTool Inner) CreateSut(
        List<UavSummary>? fleet, string rootOperatorText, string? chatReply)
    {
        var operationService = Substitute.For<IOperationService>();
        operationService.ListFleet(Arg.Any<CancellationToken>()).Returns(
            fleet is not null ? OperationResult.Ok(fleet) : OperationResult.Invalid("fleet lookup failed"));

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
        promptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, TimeSpan.FromSeconds(30));

        var inner = new FakeInnerTool();
        var tool = new FleetWideActionConfirmationTool(
            inner, operationService, promptGate, new TailNumberResolutionScope(), DelegateName, CorrelationId, rootOperatorText);

        return (tool, inner);
    }

    [Fact]
    public async Task InvokeCoreAsync_InstructionNamesEveryTail_OperatorDidNotNameThemAll_AsksConfirmation()
    {
        var (tool, inner) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly all of them to alpha", chatReply: "Yes");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1, UAV-2, and UAV-3 to alpha." }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
        result!.ToString().Should().Be("ok");
    }

    [Fact]
    public async Task InvokeCoreAsync_ConfirmationDeclined_NotDelegated()
    {
        var (tool, inner) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly all of them to alpha", chatReply: "No");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1, UAV-2, and UAV-3 to alpha." }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().BeEmpty();
        result!.ToString().Should().Contain("Not delegated").And.Contain("did not confirm");
    }

    [Fact]
    public async Task InvokeCoreAsync_ConfirmationNoReply_NotDelegated()
    {
        var (tool, inner) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly all of them to alpha", chatReply: null);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1, UAV-2, and UAV-3 to alpha." }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().BeEmpty();
        result!.ToString().Should().Contain("Not delegated");
    }

    [Fact]
    public async Task InvokeCoreAsync_OperatorNamedEveryTailThemselves_NoConfirmationNeeded()
    {
        // Matches the pre-existing, unrelated "operator explicitly named 2+ specific UAVs" path -
        // must not regress that into requiring a new confirmation.
        var (tool, inner) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly UAV-1, UAV-2, and UAV-3 to alpha", chatReply: null);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1, UAV-2, and UAV-3 to alpha." }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
        result!.ToString().Should().Be("ok");
    }

    [Fact]
    public async Task InvokeCoreAsync_InstructionNamesOnlySomeTails_NoConfirmationNeeded()
    {
        var (tool, inner) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly UAV-1 to alpha", chatReply: null);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1 to alpha." }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
        result!.ToString().Should().Be("ok");
    }

    [Fact]
    public async Task InvokeCoreAsync_FleetLookupFails_FailsOpen_StillDelegates()
    {
        var (tool, inner) = CreateSut(fleet: null, rootOperatorText: "fly all of them to alpha", chatReply: null);

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1, UAV-2, and UAV-3 to alpha." }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeCoreAsync_FleetHasOnlyOneUav_NoConfirmationNeeded()
    {
        var (tool, inner) = CreateSut([new UavSummary("UAV-1", "Orbiting", 0, 0)], rootOperatorText: "fly it to alpha", chatReply: null);

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "Fly UAV-1 to alpha." }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
    }
}

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

public class TailNumberProvenanceGuardToolTests
{
    private const string DelegateName = "FlightControlAgent";
    private const string CorrelationId = "corr1";

    /// <summary>Minimal inner AIFunction standing in for DelegateAgentTool - records the
    /// "instruction" it was actually invoked with, and optionally simulates the delegated
    /// specialist internally calling a real leaf tool (which, in production, is what actually
    /// increments ToolInvocationLogger's per-agent count - the delegation call itself is logged
    /// under the *parent* agent's name, not the delegate's, so simulating that nested call is what
    /// makes "did the specialist stay silent" observable to the guard under test).</summary>
    private sealed class FakeInnerTool(ToolInvocationLogger toolLogger, Func<int, bool> simulateRealToolCall) : AIFunction
    {
        public List<string> InvokedInstructions { get; } = [];

        public override string Name => DelegateName;
        public override string Description => "Handles flight.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            arguments.TryGetValue("instruction", out var raw);
            var instruction = raw?.ToString() ?? "";
            var callIndex = InvokedInstructions.Count;
            InvokedInstructions.Add(instruction);

            if (simulateRealToolCall(callIndex))
            {
                return await toolLogger.LogAsync(CorrelationId, DelegateName, "SetSpeed", arguments,
                    () => Task.FromResult<object?>($"ok:{instruction}"), r => r?.ToString() ?? "");
            }

            return "I need to know which UAV you're referring to.";
        }
    }

    private static List<UavSummary> ThreeUavFleet() =>
    [
        new UavSummary("UAV-1", "Orbiting", 0, 0),
        new UavSummary("UAV-2", "Orbiting", 0, 0),
        new UavSummary("UAV-3", "Orbiting", 0, 0)
    ];

    private static (TailNumberProvenanceGuardTool Tool, FakeInnerTool Inner, ToolInvocationLogger ToolLogger) CreateSut(
        List<UavSummary>? fleet, string rootOperatorText, bool simulateRealToolCall = true, string? chatReply = null,
        Func<int, bool>? simulateRealToolCallByIndex = null)
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

        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var inner = new FakeInnerTool(toolLogger, simulateRealToolCallByIndex ?? (_ => simulateRealToolCall));
        var tool = new TailNumberProvenanceGuardTool(
            inner, operationService, promptGate, new TailNumberResolutionScope(), toolLogger, DelegateName, CorrelationId, rootOperatorText);

        return (tool, inner, toolLogger);
    }

    [Fact]
    public async Task InvokeCoreAsync_InstructionNamesOperatorSpecifiedUav_Delegates()
    {
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly UAV-2 to target alpha");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "fly UAV-2 to target alpha" }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle().Which.Should().Contain("UAV-2");
        result!.ToString().Should().Contain("ok:");
    }

    [Fact]
    public async Task InvokeCoreAsync_InstructionNamesRealTailNumberOperatorNeverSaid_BlocksDelegation()
    {
        // Regression: MoavAgent guessing a tail number itself (e.g. after a garbled delegate
        // response) and baking it into the re-delegated instruction - must be blocked
        // deterministically without assuming any tail-number naming format.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "set speed to 250 and altitude to 3000");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250 and altitude to 3000 for UAV-1" }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().BeEmpty();
        result!.ToString().Should().Contain("Not delegated").And.Contain("UAV-1");
    }

    [Fact]
    public async Task InvokeCoreAsync_FleetLookupFails_FailsOpen_StillDelegates()
    {
        // Can't determine groundedness without a real fleet list to check against - fall through
        // rather than block on a guard that can't help, same reasoning as
        // TailNumberDisambiguationTool's own fleet-lookup-failure fallthrough.
        var (tool, inner, _) = CreateSut(fleet: null, rootOperatorText: "set speed to 250 and altitude to 3000");

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250 and altitude to 3000 for UAV-1" }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeCoreAsync_TailNumberDoesNotMatchAnyRealFleetTail_Delegates()
    {
        // A string that merely looks like it could be a tail number, but isn't one of the real
        // known fleet tails, is not MoavAgent inventing a real value - nothing to block.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "set speed to 250 for UAV-99");

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250 for UAV-99" }),
            CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeCoreAsync_SpecialistCallsRealTool_NoTailNumberInInstruction_NoFallbackAsk()
    {
        // The specialist itself resolved the ambiguity by calling a real tool (e.g. it recognized
        // "ALL" and fanned out) - the guard must not second-guess a specialist that actually acted.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "set speed to 250", simulateRealToolCall: true);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250" }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle(); // no retry
        result!.ToString().Should().Contain("ok:");
    }

    [Fact]
    public async Task InvokeCoreAsync_SpecialistStaysSilent_AmbiguousInstruction_ForcesStructuredAskAndRetries()
    {
        // Regression for case 1: FlightControlAgent answering "I need to know which UAV..." in
        // plain text, calling no tool at all, must not be relayed as-is when the instruction never
        // named a real tail number - a real structured prompt is forced instead, then the same
        // instruction is retried once with the operator's real answer appended.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "set speed to 250", chatReply: "UAV-2",
            simulateRealToolCallByIndex: callIndex => callIndex > 0); // silent on the first (ambiguous) attempt, succeeds on the retry

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250" }), CancellationToken.None);

        inner.InvokedInstructions.Should().HaveCount(2);
        inner.InvokedInstructions[0].Should().Be("set speed to 250"); // the silent first attempt
        inner.InvokedInstructions[1].Should().Contain("set speed to 250").And.Contain("UAV-2"); // the retry
        result!.ToString().Should().Contain("ok:");
    }

    [Fact]
    public async Task InvokeCoreAsync_SpecialistStaysSilent_NoValidReply_ReturnsNotDelegated()
    {
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "set speed to 250", simulateRealToolCall: false, chatReply: null);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250" }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle(); // no retry attempted
        result!.ToString().Should().Contain("Not delegated");
    }

    [Fact]
    public async Task InvokeCoreAsync_SpecialistStaysSilent_ButInstructionAlreadyNamesUav_NoFallbackAsk()
    {
        // Silence alone isn't enough to trigger the fallback - only silence on a genuinely
        // ambiguous instruction. If a real tail number is already present, there's nothing to ask.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly UAV-1 to target alpha", simulateRealToolCall: false);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "fly UAV-1 to target alpha" }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle();
        result!.ToString().Should().Contain("I need to know");
    }

    [Fact]
    public async Task InvokeCoreAsync_TailNumberGroundedInDelegatedInstruction_NotInRootOperatorText_Delegates()
    {
        // BrainAgent now carries real multi-turn memory and may resolve a cross-turn reference
        // itself before ever delegating to MoavAgent (e.g. operator said "fly it to alpha" this
        // turn, but BrainAgent resolved "it" -> UAV-1 from an earlier turn and handed MoavAgent
        // "fly UAV-1 to alpha"). The real tail number MoavAgent then relays to FlightControlAgent
        // must not be flagged as invented just because it isn't in this turn's raw operator text -
        // DelegatedInstructionContext.Current (what MoavAgent was actually given) is the correct
        // ground truth here, not the root operator text alone.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "fly it to alpha");

        using var _ = DelegatedInstructionContext.Push("fly UAV-1 to alpha");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "fly UAV-1 to alpha" }), CancellationToken.None);

        inner.InvokedInstructions.Should().ContainSingle().Which.Should().Contain("UAV-1");
        result!.ToString().Should().Contain("ok:");
    }

    [Fact]
    public async Task InvokeCoreAsync_TailNumberInNeitherDelegatedInstructionNorRootText_StillBlocks()
    {
        // The DelegatedInstructionContext fallback must not turn into a free pass - a tail number
        // absent from both the text MoavAgent was actually given and the root operator text is
        // still an invented value and must still be blocked.
        var (tool, inner, _) = CreateSut(ThreeUavFleet(), rootOperatorText: "set speed to 250");

        using var _ = DelegatedInstructionContext.Push("set speed to 250");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "set speed to 250 for UAV-3" }), CancellationToken.None);

        inner.InvokedInstructions.Should().BeEmpty();
        result!.ToString().Should().Contain("Not delegated").And.Contain("UAV-3");
    }
}

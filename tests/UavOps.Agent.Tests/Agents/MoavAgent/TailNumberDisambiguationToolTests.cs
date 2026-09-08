using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

public class TailNumberDisambiguationToolTests
{
    /// <summary>A minimal inner AIFunction standing in for the wrapped fleet tool (an
    /// MCP-backed one in production - see <see cref="Tooling.McpBackedTool"/>) - just records the
    /// tailNumber it was actually invoked with, so tests can assert exactly which UAV(s) execution
    /// reached without needing a real MCP round trip.</summary>
    private sealed class FakeInnerTool(string name = "SetSpeed") : AIFunction
    {
        public List<string?> InvokedTailNumbers { get; } = [];

        public override string Name => name;
        public override string Description => "Change a UAV's speed.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            arguments.TryGetValue("tailNumber", out var raw);
            var tailNumber = raw?.ToString();
            InvokedTailNumbers.Add(tailNumber);
            return ValueTask.FromResult<object?>($"ok:{tailNumber}");
        }
    }

    private static List<UavSummary> ThreeUavFleet() =>
    [
        new UavSummary("UAV-1", "Orbiting", 0, 0),
        new UavSummary("UAV-2", "Orbiting", 0, 0),
        new UavSummary("UAV-3", "Orbiting", 0, 0)
    ];

    /// <summary>Builds a real TailNumberDisambiguationTool wired to a hub mock that reacts to the
    /// operator prompt exactly like ChatHub does - mirrors AskOperatorChoiceToolTests/
    /// OperationToolTests's CreateSutWithConfirmation, since this tool needs to exercise
    /// InvokeCoreAsync itself, including its own ask-and-wait round trip.</summary>
    private static (TailNumberDisambiguationTool Tool, FakeInnerTool Inner, Func<CancellationToken, Task<OperationResult>> ListFleet, IClientProxy Proxy) CreateSut(
        List<UavSummary>? fleet, string? chatReply, string operatorText = "", TailNumberResolutionScope? scope = null)
    {
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

        Task<OperationResult> ListFleet(CancellationToken cancellationToken) => Task.FromResult(
            fleet is not null ? OperationResult.Ok(fleet) : OperationResult.Invalid("fleet lookup failed"));

        var inner = new FakeInnerTool();
        var tool = new TailNumberDisambiguationTool(
            inner, ListFleet, promptGate, scope ?? new TailNumberResolutionScope(),
            "FlightControlAgent", "corr1", operatorText);

        return (tool, inner, ListFleet, proxy);
    }

    [Fact]
    public async Task InvokeCoreAsync_AllSentinel_FleetHasMultipleUavs_FansOutWithNoConfirmation()
    {
        // TEMPORARY, FOR TESTING: the "apply to all N known UAVs?" confirmation was removed at the
        // operator's explicit request (see ResolveAllUavsRequestAsync's doc comment) to match the
        // flat single-agent lab's own no-guard behavior while testing this architecture - the
        // model's own "ALL" recognition now fans out directly, no operator round trip at all.
        var (tool, inner, _, proxy) = CreateSut(ThreeUavFleet(), chatReply: null);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "ALL" }), CancellationToken.None);

        await proxy.DidNotReceive().SendCoreAsync("ReceiveChoices", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
        result!.ToString().Should().Contain("UAV-1:").And.Contain("UAV-2:").And.Contain("UAV-3:");
    }

    [Fact]
    public async Task InvokeCoreAsync_AllSentinel_CaseInsensitive_FansOut()
    {
        var (tool, inner, _, _) = CreateSut(ThreeUavFleet(), chatReply: null);

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "all" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_SameToolCalledTwiceThisTurn_BothResolveToAll_OnlyFansOutOnce()
    {
        // Direct regression test for a live-reproduced bug: the model issuing the same tool call
        // multiple times in one completion (each with an unresolved tailNumber) used to
        // independently re-fan-out across the whole fleet every time it resolved to "ALL" -
        // multiplying real mutations (e.g. 3 duplicate calls x 3 known UAVs = 9 real invocations
        // instead of 3). TailNumberResolutionScope.GetOrFanOutAsync now dedupes the fan-out
        // execution itself per tool name, not just the shared "which UAV"/"ALL" answer.
        var (tool, inner, _, _) = CreateSut(ThreeUavFleet(), chatReply: null);
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "ALL" });

        await tool.InvokeAsync(args, CancellationToken.None);
        await tool.InvokeAsync(args, CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_SameToolCalledConcurrentlyThisTurn_BothResolveToAll_OnlyFansOutOnce()
    {
        // Same bug, but exercising the concurrent path (AllowConcurrentInvocation runs tool calls
        // within one turn concurrently in production) - TailNumberResolutionScope's lock must
        // guarantee the fan-out factory runs at most once even when both calls race.
        var (tool, inner, _, _) = CreateSut(ThreeUavFleet(), chatReply: null);
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "ALL" });

        await Task.WhenAll(
            tool.InvokeAsync(args, CancellationToken.None).AsTask(),
            tool.InvokeAsync(args, CancellationToken.None).AsTask());

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_AllSentinel_FleetHasExactlyOneUav_ResolvesDirectly_NeverSendsLiteralAll()
    {
        var (tool, inner, _, _) = CreateSut([new UavSummary("UAV-1", "Orbiting", 0, 0)], chatReply: null);

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "ALL" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_AllSentinel_FleetLookupFails_ReturnsNotExecuted_NeverInvokesInner()
    {
        var (tool, inner, _, _) = CreateSut(fleet: null, chatReply: null);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "ALL" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEmpty();
        result!.ToString().Should().Contain("Not executed");
    }

    [Fact]
    public async Task InvokeCoreAsync_ExplicitTailNumberInOperatorText_StillTrustedWithoutAsking()
    {
        // Regression guard for requirement 3: an explicitly-named tail number must still be
        // trusted directly, with no ask and no "ALL" handling involved at all.
        var (tool, inner, _, proxy) = CreateSut(ThreeUavFleet(), chatReply: null, operatorText: "UAV-2 set speed to 250");

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-2" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-2"]);
        await proxy.DidNotReceive().SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeCoreAsync_UngroundedGuess_OffersAllAsAChoice_OperatorSelectsIt_FansOut()
    {
        List<string>? offeredChoices = null;
        var (tool, inner, _, proxy) = CreateSut(ThreeUavFleet(), chatReply: "ALL", operatorText: "set speed to 250");
        proxy.When(p => p.SendCoreAsync("ReceiveChoices", Arg.Any<object?[]>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => offeredChoices = ((IReadOnlyList<string>)callInfo.ArgAt<object?[]>(1)[1]!).ToList());

        // Model guessed "UAV-1" even though the operator never named a UAV - ungrounded.
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        offeredChoices.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3", "ALL"]);
        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
        result!.ToString().Should().Contain("UAV-1:").And.Contain("UAV-2:").And.Contain("UAV-3:");
    }

    [Fact]
    public async Task InvokeCoreAsync_UngroundedGuess_OperatorSelectsSpecificUav_ConfirmsTargetInResult()
    {
        var (tool, inner, _, _) = CreateSut(ThreeUavFleet(), chatReply: "UAV-3", operatorText: "set speed to 250");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-3"]);
        result!.ToString().Should().Contain("UAV-3").And.Contain("ONLY UAV-3");
    }

    [Fact]
    public async Task InvokeCoreAsync_SharedScope_SecondCallReusesAllResolution_WithoutAskingAgain()
    {
        // Mirrors how SetSpeed and SetAltitude share one TailNumberResolutionScope for the same
        // FlightControlAgent turn (see AgentFactory.BuildAgentTools) - once the operator answers
        // "ALL" for the first tailNumber-taking call, a second one in the same turn must reuse it.
        var scope = new TailNumberResolutionScope();
        var (firstTool, firstInner, _, proxy) = CreateSut(ThreeUavFleet(), chatReply: "ALL", operatorText: "set speed to 250", scope: scope);

        await firstTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        // Second tool instance - a genuinely different operation (SetAltitude, not SetSpeed) -
        // sharing the same scope. Reusing the fresh proxy/promptGate wiring isn't possible via
        // CreateSut (it builds its own hub), so build the second tool directly against the same
        // scope's already-resolved value. Distinct Name matters now: TailNumberResolutionScope
        // dedupes fan-out execution per tool name (see its own doc comment) - this test must prove
        // two DIFFERENT actions each still get their own independent fan-out, not that a second call
        // to the SAME action is deduped (that's InvokeCoreAsync_SameToolCalled*_OnlyFansOutOnce).
        var secondInner = new FakeInnerTool("SetAltitude");
        Task<OperationResult> secondListFleet(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok(ThreeUavFleet()));
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var secondPromptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, TimeSpan.FromMilliseconds(200));
        var secondTool = new TailNumberDisambiguationTool(
            secondInner, secondListFleet, secondPromptGate, scope, "FlightControlAgent", "corr1", "set speed to 250");

        await secondTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        firstInner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
        secondInner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_TwoCallsGuessDifferentTailNumbers_EachResolvesIndependently_NeitherClobbersTheOther()
    {
        // Direct regression test for a live-reported bug: operator says "bring the other two home"
        // (no tail number literally in the text) and the model issues two ReturnToLaunch calls in
        // the same turn, guessing UAV-2 then UAV-3 - two genuinely different intended targets, not
        // one ambiguous UAV asked about twice. The old un-keyed TailNumberResolutionScope answered
        // only the FIRST ask and silently applied that same answer to the second call too, so both
        // UAVs ended up commanded against whatever was asked about first instead of their own real,
        // different targets - exactly the transcript the operator reported (four ReturnToLaunch
        // calls, all executed against UAV-1). Keying the scope by the model's own guessed value
        // fixes this: two different guesses must each get their own independent prompt.
        var scope = new TailNumberResolutionScope();
        var fleet = ThreeUavFleet();
        var replies = new Queue<string>(["UAV-2", "UAV-3"]);
        OperatorPromptGate? promptGate = null;
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // RequestChoiceAsync sends TWO ReceiveChatMessage events per prompt - the question
                // itself, then a "Got it - proceeding with: X" confirmation once resolved. Only the
                // question (identifiable by "Options:") should consume a queued reply - otherwise
                // the first prompt's own confirmation message wrongly steals the SECOND prompt's
                // reply before it's even been asked.
                var message = callInfo.ArgAt<object?[]>(1)[1] as string ?? "";
                if (message.Contains("Options:") && replies.Count > 0)
                {
                    _ = promptGate!.TryHandleChatReplyAsync(replies.Dequeue(), CancellationToken.None);
                }
                return Task.CompletedTask;
            });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);
        promptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, TimeSpan.FromSeconds(30));

        Task<OperationResult> ListFleet(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok(fleet));

        var inner = new FakeInnerTool("ReturnToLaunch");
        var tool = new TailNumberDisambiguationTool(
            inner, ListFleet, promptGate, scope, "FlightControlAgent", "corr1", "bring the other two home");

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-2" }), CancellationToken.None);
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-3" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_CommaSeparatedTailNumbers_InvokesExactlyThatSubset_NoAsking()
    {
        // Option 2 fix for the same live incident: rather than relying on the model to reliably
        // issue one separate tool call per remaining UAV (proven unreliable - see the test above's
        // own comment and the live ReturnRemainingFleet scenario, which failed 6/8 times), the model
        // computes the exact subset itself and passes it as one comma-separated value. This must
        // execute against exactly that subset, with no operator prompt at all - the model already
        // did the resolution.
        var (tool, inner, _, proxy) = CreateSut(ThreeUavFleet(), chatReply: null, operatorText: "bring the rest UAVs home");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-2,UAV-3" }), CancellationToken.None);

        await proxy.DidNotReceive().SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-2", "UAV-3"]);
        result!.ToString().Should().Contain("UAV-2:").And.Contain("UAV-3:").And.NotContain("UAV-1:");
    }

    [Fact]
    public async Task InvokeCoreAsync_CommaSeparatedTailNumbers_IgnoresUnknownEntries_KeepsValidOnes()
    {
        var (tool, inner, _, _) = CreateSut(ThreeUavFleet(), chatReply: null, operatorText: "bring the rest UAVs home");

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-2,UAV-9,UAV-3" }), CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_CommaSeparatedTailNumbers_CalledTwiceThisTurn_OnlyFansOutOnce()
    {
        // Same duplicate-call protection GetOrFanOutAsync already gives the "ALL" and single-UAV
        // paths, extended to an explicit subset - a repeat of the identical subset this turn must
        // not re-execute.
        var (tool, inner, _, _) = CreateSut(ThreeUavFleet(), chatReply: null, operatorText: "bring the rest UAVs home");
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-2,UAV-3" });

        await tool.InvokeAsync(args, CancellationToken.None);
        await tool.InvokeAsync(args, CancellationToken.None);

        inner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-2", "UAV-3"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_DifferentCommaSeparatedSubsets_BothExecuteIndependently()
    {
        var scope = new TailNumberResolutionScope();
        var (firstTool, firstInner, _, _) = CreateSut(ThreeUavFleet(), chatReply: null, operatorText: "bring the rest home", scope: scope);
        await firstTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-2,UAV-3" }), CancellationToken.None);

        var secondInner = new FakeInnerTool("SetSpeed");
        Task<OperationResult> secondListFleet(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok(ThreeUavFleet()));
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var secondPromptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, TimeSpan.FromMilliseconds(200));
        var secondTool = new TailNumberDisambiguationTool(
            secondInner, secondListFleet, secondPromptGate, scope, "FlightControlAgent", "corr1", "set the rest to 250");
        await secondTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1,UAV-3" }), CancellationToken.None);

        firstInner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-2", "UAV-3"]);
        secondInner.InvokedTailNumbers.Should().BeEquivalentTo(["UAV-1", "UAV-3"]);
    }
}

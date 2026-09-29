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

public class TailNumbersGroundingToolTests
{
    /// <summary>Records each call's tailNumbers list, so tests see exactly what reached the tool.</summary>
    private sealed class FakeInnerTool : AIFunction
    {
        public List<string[]> Calls { get; } = [];

        public override string Name => "PrepareAoiSearch";
        public override string Description => "Search a zone.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var tails = (string[])arguments["tailNumbers"]!;
            Calls.Add(tails);
            return ValueTask.FromResult<object?>($"ok:{string.Join(",", tails)}");
        }
    }

    private static readonly List<UavSummary> Fleet =
    [
        new("997", "Orbiting", 0, 0),
        new("998", "Orbiting", 0, 0),
        new("999", "Orbiting", 0, 0)
    ];

    private sealed record Sut(TailNumbersGroundingTool Tool, FakeInnerTool Inner, List<string> Prompts, FleetGroupMemory Groups, OperatorUavContext Context);

    /// <summary>A real tool over a hub mock that answers the operator prompt like ChatHub does.</summary>
    private static Sut CreateSut(string operatorText, string? reply = null, OperatorUavContext? context = null)
    {
        OperatorPromptGate? gate = null;
        var prompts = new List<string>();
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var text = (string)call.ArgAt<object?[]>(1)[1]!;
                if (text.Contains("Options:"))
                {
                    prompts.Add(text);
                    if (reply is not null)
                        _ = gate!.TryHandleChatReplyAsync(reply, CancellationToken.None);
                }
                return Task.CompletedTask;
            });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);
        gate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance, TimeSpan.FromSeconds(5));

        var inner = new FakeInnerTool();
        var groups = new FleetGroupMemory();
        context ??= new OperatorUavContext();
        var tool = new TailNumbersGroundingTool(inner, _ => Task.FromResult(OperationResult.Ok(Fleet)), gate,
            new TailNumberResolutionScope(), groups, context, "BrainAgent", "corr1", operatorText);
        return new Sut(tool, inner, prompts, groups, context);
    }

    private static AIFunctionArguments Args(object tailNumbers) => new() { ["tailNumbers"] = tailNumbers, ["zoneName"] = "ZoneA" };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task NamedPair_IsOneCallWithBoth_NoPrompt()
    {
        var sut = CreateSut("send 998 and 999 to search for a red car in ZoneA");

        var result = await sut.Tool.InvokeAsync(Args(Json("""["998","999"]""")));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("998", "999");
        sut.Prompts.Should().BeEmpty();
        result!.ToString().Should().Be("ok:998,999");
        sut.Groups.GetLastFullGroup().Should().BeEquivalentTo(["998", "999"]);
    }

    [Fact]
    public async Task GroupWord_TrustsTheModelsList_NoPrompt()
    {
        var sut = CreateSut("search ZoneA for a red car with all UAVs");

        await sut.Tool.InvokeAsync(Args(Json("""["997","998","999"]""")));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("997", "998", "999");
        sut.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task SeveralUavsTheOperatorNeverAskedFor_AreConfirmedFirst()
    {
        var sut = CreateSut("search ZoneA for a red car", reply: "No");

        var result = await sut.Tool.InvokeAsync(Args(Json("""["997","998","999"]""")));

        sut.Prompts.Should().ContainSingle().Which.Should().Contain("997, 998 and 999");
        sut.Inner.Calls.Should().BeEmpty();
        result!.ToString().Should().StartWith("Not executed");
    }

    [Fact]
    public async Task SeveralUavsConfirmed_RunOnce()
    {
        var sut = CreateSut("search ZoneA for a red car", reply: "Yes");

        await sut.Tool.InvokeAsync(Args(Json("""["998","999"]""")));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("998", "999");
    }

    [Theory]
    [InlineData("""["ALL"]""")]
    [InlineData("""["997","all"]""")]
    public async Task All_IsNeverExecuted(string list)
    {
        var sut = CreateSut("search ZoneA with all UAVs");

        var result = await sut.Tool.InvokeAsync(Args(Json(list)));

        sut.Inner.Calls.Should().BeEmpty();
        result!.ToString().Should().StartWith("Not executed");
    }

    [Fact]
    public async Task EmptyList_AsksTheOperatorWhichUav()
    {
        var sut = CreateSut("search ZoneA for a white van", reply: "997");

        await sut.Tool.InvokeAsync(Args(Json("[]")));

        sut.Prompts.Should().ContainSingle().Which.Should().Contain("Which UAV").And.NotContain("ALL");
        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("997");
        sut.Context.Current.Should().Be("997");
    }

    [Fact]
    public async Task OneUnnamedUav_AsksWhichOne_WithoutAnAllChoice()
    {
        var sut = CreateSut("search ZoneA for a white van", reply: "998");

        var result = await sut.Tool.InvokeAsync(Args(Json("""["997"]""")));

        sut.Prompts.Should().ContainSingle().Which.Should().Contain("Which UAV").And.NotContain("ALL");
        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("998");
        result!.ToString().Should().Contain("ONLY 998");
        sut.Context.Current.Should().Be("998");
    }

    [Fact]
    public async Task OneNamedUav_RunsDirectly_AndBecomesTheCurrentUav()
    {
        var sut = CreateSut("enter zone a with 997 and search for a white van");

        await sut.Tool.InvokeAsync(Args(Json("""["997"]""")));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("997");
        sut.Prompts.Should().BeEmpty();
        sut.Context.Current.Should().Be("997");
    }

    [Fact]
    public async Task TheOperatorsCurrentUav_GroundsAnUnnamedFollowUp()
    {
        var context = new OperatorUavContext();
        context.Set("997");
        var sut = CreateSut("now search zone b for a red car", context: context);

        await sut.Tool.InvokeAsync(Args(Json("""["997"]""")));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("997");
        sut.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownTails_AreDroppedAndNamed()
    {
        var sut = CreateSut("send 998 and 999 and 123 to search ZoneA");

        var result = await sut.Tool.InvokeAsync(Args(Json("""["998","999","123"]""")));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("998", "999");
        result!.ToString().Should().StartWith("Note - 123 is not a known UAV");
    }

    [Theory]
    [InlineData("998,999")]
    [InlineData("998, 999")]
    public async Task AStringInsteadOfAList_IsTakenAsTheList(string value)
    {
        var sut = CreateSut("send 998 and 999 to search ZoneA");

        await sut.Tool.InvokeAsync(Args(Json(JsonSerializer.Serialize(value))));

        sut.Inner.Calls.Should().ContainSingle().Which.Should().Equal("998", "999");
    }
}

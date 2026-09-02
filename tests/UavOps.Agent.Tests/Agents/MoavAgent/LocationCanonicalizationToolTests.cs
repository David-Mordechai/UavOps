using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MoavAgent;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MoavAgent;

public class LocationCanonicalizationToolTests
{
    private sealed class FakeInnerTool : AIFunction
    {
        public AIFunctionArguments? LastArguments { get; private set; }

        public override string Name => "Navigate";
        public override string Description => "Navigate.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            LastArguments = arguments;
            return ValueTask.FromResult<object?>("ok");
        }
    }

    [Theory]
    [InlineData("target alpha", "alpha")]
    [InlineData("Target Alpha", "Alpha")]
    [InlineData("TARGET   alpha", "alpha")]
    [InlineData("target bravo", "bravo")]
    public async Task InvokeCoreAsync_StripsTargetPrefix_BeforeInnerToolSees(string given, string expected)
    {
        var inner = new FakeInnerTool();
        var tool = new LocationCanonicalizationTool(inner);

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["location"] = given }), CancellationToken.None);

        inner.LastArguments!["location"].Should().Be(expected);
    }

    [Fact]
    public async Task InvokeCoreAsync_RewritesArgument_GroundsTheModelsOwnSummaryWithACorrectionNote()
    {
        // Rewriting the argument alone isn't enough - the model still writes its own free-text
        // summary from what it recalls saying, not from the tool result (observed live: the call
        // correctly used "alpha" but the model's own prose kept saying "target alpha" regardless).
        // The result text must force the correction, same pattern as
        // TailNumberDisambiguationTool's BuildConfirmedTargetNote for tail numbers.
        var inner = new FakeInnerTool();
        var tool = new LocationCanonicalizationTool(inner);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["location"] = "target alpha" }), CancellationToken.None);

        result!.ToString().Should().Contain("alpha").And.Contain("target alpha").And.Contain("ok");
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("bravo")]
    [InlineData("home")]
    public async Task InvokeCoreAsync_BareName_PassesThroughUnchanged(string location)
    {
        var inner = new FakeInnerTool();
        var tool = new LocationCanonicalizationTool(inner);

        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["location"] = location }), CancellationToken.None);

        inner.LastArguments!["location"].Should().Be(location);
    }

    [Fact]
    public async Task InvokeCoreAsync_NoLocationArgument_Delegates()
    {
        var inner = new FakeInnerTool();
        var tool = new LocationCanonicalizationTool(inner);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        result!.ToString().Should().Be("ok");
        inner.LastArguments!.ContainsKey("location").Should().BeFalse();
    }
}

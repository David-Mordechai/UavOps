using System.Text.Json;
using FluentAssertions;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Options;

public class AgentGraphProjectorTests
{
    // AgentGraphProjector.Build returns anonymous objects, so tests round-trip through JSON to
    // inspect the shape by property name rather than depending on the anonymous types directly.
    private static JsonElement BuildAsJson(AgentConfig config)
    {
        var json = JsonSerializer.Serialize(AgentGraphProjector.Build(config));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Build_SingleFlatAgent_ProducesRootNodePlusOneToolNodePerTool()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig
                {
                    Operation = "SetSpeed",
                    Description = "Change speed.",
                    ExampleUtterance = "set UAV-1's speed to 250 knots",
                    Parameters = new Dictionary<string, string> { ["tailNumber"] = "the tail number", ["speedKts"] = "the speed" },
                    FixedParameters = new Dictionary<string, string> { ["mode"] = "cruise" }
                }
            ]
        };

        var root = BuildAsJson(config);
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        var edges = root.GetProperty("edges").EnumerateArray().ToList();

        var brainNode = nodes.Single(n => n.GetProperty("id").GetString() == "BrainAgent");
        brainNode.GetProperty("type").GetString().Should().Be("agent");
        brainNode.GetProperty("isRoot").GetBoolean().Should().BeTrue();

        var toolNode = nodes.Single(n => n.GetProperty("id").GetString() == "BrainAgent::SetSpeed");
        toolNode.GetProperty("type").GetString().Should().Be("tool");
        toolNode.GetProperty("ownerAgent").GetString().Should().Be("BrainAgent");
        toolNode.GetProperty("operation").GetString().Should().Be("SetSpeed");
        toolNode.GetProperty("description").GetString().Should().Be("Change speed.");
        toolNode.GetProperty("exampleUtterance").GetString().Should().Be("set UAV-1's speed to 250 knots");

        edges.Should().ContainSingle(e =>
            e.GetProperty("from").GetString() == "BrainAgent" &&
            e.GetProperty("to").GetString() == "BrainAgent::SetSpeed" &&
            e.GetProperty("kind").GetString() == "uses");
    }

    [Fact]
    public void Build_NoTools_OnlyRootNodeAndNoEdges()
    {
        var config = new AgentConfig { Instructions = "x" };

        var root = BuildAsJson(config);
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        var edges = root.GetProperty("edges").EnumerateArray().ToList();

        nodes.Should().ContainSingle();
        nodes.Single().GetProperty("id").GetString().Should().Be("BrainAgent");
        edges.Should().BeEmpty();
    }

    [Fact]
    public void Build_ToolParameters_ExcludesFixedParameters()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig
                {
                    Operation = "DoThing",
                    Description = "x",
                    ExampleUtterance = "x",
                    Parameters = new Dictionary<string, string> { ["visible"] = "shown to the LLM" },
                    FixedParameters = new Dictionary<string, string> { ["hidden"] = "never shown" }
                }
            ]
        };

        var root = BuildAsJson(config);
        var toolNode = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "tool");
        var parameters = toolNode.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()).ToList();

        parameters.Should().BeEquivalentTo(["visible"]);
        parameters.Should().NotContain("hidden");
    }
}

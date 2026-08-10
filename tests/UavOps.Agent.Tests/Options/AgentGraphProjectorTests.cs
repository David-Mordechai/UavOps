using System.Text.Json;
using FluentAssertions;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Options;

public class AgentGraphProjectorTests
{
    // AgentGraphProjector.Build returns anonymous objects, so tests round-trip through JSON to
    // inspect the shape by property name rather than depending on the anonymous types directly.
    private static JsonElement BuildAsJson(Dictionary<string, AgentConfig> agents)
    {
        var json = JsonSerializer.Serialize(AgentGraphProjector.Build(agents));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Build_AgentWithChildAndTool_ProducesExpectedNodesAndEdges()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "x", Children = ["FlightControlAgent"] },
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "Handles flight controls.",
                ExampleUtterance = "set UAV-1's speed to 250 knots",
                Children = [],
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
            }
        };

        var root = BuildAsJson(agents);
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        var edges = root.GetProperty("edges").EnumerateArray().ToList();

        var brainNode = nodes.Single(n => n.GetProperty("id").GetString() == "BrainAgent");
        brainNode.GetProperty("type").GetString().Should().Be("agent");
        brainNode.GetProperty("isRoot").GetBoolean().Should().BeTrue();

        var flightNode = nodes.Single(n => n.GetProperty("id").GetString() == "FlightControlAgent");
        flightNode.GetProperty("isRoot").GetBoolean().Should().BeFalse();
        flightNode.GetProperty("retrievalBased").GetBoolean().Should().BeFalse();
        flightNode.GetProperty("description").GetString().Should().Be("Handles flight controls.");
        flightNode.GetProperty("exampleUtterance").GetString().Should().Be("set UAV-1's speed to 250 knots");

        var toolNode = nodes.Single(n => n.GetProperty("id").GetString() == "FlightControlAgent::SetSpeed");
        toolNode.GetProperty("type").GetString().Should().Be("tool");
        toolNode.GetProperty("ownerAgent").GetString().Should().Be("FlightControlAgent");
        toolNode.GetProperty("operation").GetString().Should().Be("SetSpeed");

        edges.Should().ContainSingle(e =>
            e.GetProperty("from").GetString() == "BrainAgent" &&
            e.GetProperty("to").GetString() == "FlightControlAgent" &&
            e.GetProperty("kind").GetString() == "delegates");

        edges.Should().ContainSingle(e =>
            e.GetProperty("from").GetString() == "FlightControlAgent" &&
            e.GetProperty("to").GetString() == "FlightControlAgent::SetSpeed" &&
            e.GetProperty("kind").GetString() == "uses");
    }

    [Fact]
    public void Build_AgentWithoutChildren_IsMarkedRetrievalBasedAndGetsNoDelegateEdges()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["SomeAgent"] = new AgentConfig { Instructions = "x", Description = "x", ExampleUtterance = "x" } // Children omitted
        };

        var root = BuildAsJson(agents);
        var node = root.GetProperty("nodes").EnumerateArray().Single();
        var edges = root.GetProperty("edges").EnumerateArray().ToList();

        node.GetProperty("retrievalBased").GetBoolean().Should().BeTrue();
        edges.Should().BeEmpty();
    }

    [Fact]
    public void Build_ToolParameters_ExcludesFixedParameters()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["Agent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "x",
                ExampleUtterance = "x",
                Children = [],
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
            }
        };

        var root = BuildAsJson(agents);
        var toolNode = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "tool");
        var parameters = toolNode.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()).ToList();

        parameters.Should().BeEquivalentTo(["visible"]);
        parameters.Should().NotContain("hidden");
    }
}

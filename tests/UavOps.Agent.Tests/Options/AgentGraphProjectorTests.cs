using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Options;

public class AgentGraphProjectorTests
{
    // AgentGraphProjector.Build returns anonymous objects, so tests round-trip through JSON to
    // inspect the shape by property name rather than depending on the anonymous types directly.
    private static JsonElement BuildAsJson(IReadOnlyList<(string ServerName, IReadOnlyList<AIFunction> Tools)> groups)
    {
        var json = JsonSerializer.Serialize(AgentGraphProjector.Build(groups));
        return JsonDocument.Parse(json).RootElement;
    }

    private static AIFunction FakeTool(string name, string description, string? tailNumberDescription = null) =>
        tailNumberDescription is null
            ? AIFunctionFactory.Create(() => "ok", name: name, description: description)
            : AIFunctionFactory.Create((string tailNumber) => "ok", name: name, description: description);

    [Fact]
    public void Build_OneServerOneTool_ProducesRootServerAndToolNodes()
    {
        var groups = new List<(string ServerName, IReadOnlyList<AIFunction> Tools)>
        {
            ("moav", [FakeTool("SetSpeed", "Change speed.", tailNumberDescription: "the tail number")])
        };

        var root = BuildAsJson(groups);
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        var edges = root.GetProperty("edges").EnumerateArray().ToList();

        var brainNode = nodes.Single(n => n.GetProperty("id").GetString() == "BrainAgent");
        brainNode.GetProperty("type").GetString().Should().Be("agent");
        brainNode.GetProperty("isRoot").GetBoolean().Should().BeTrue();

        var serverNode = nodes.Single(n => n.GetProperty("id").GetString() == "server::moav");
        serverNode.GetProperty("type").GetString().Should().Be("server");
        serverNode.GetProperty("name").GetString().Should().Be("moav");

        var toolNode = nodes.Single(n => n.GetProperty("id").GetString() == "server::moav::SetSpeed");
        toolNode.GetProperty("type").GetString().Should().Be("tool");
        toolNode.GetProperty("ownerServer").GetString().Should().Be("moav");
        toolNode.GetProperty("operation").GetString().Should().Be("SetSpeed");
        toolNode.GetProperty("description").GetString().Should().Be("Change speed.");

        edges.Should().ContainSingle(e =>
            e.GetProperty("from").GetString() == "BrainAgent" &&
            e.GetProperty("to").GetString() == "server::moav" &&
            e.GetProperty("kind").GetString() == "connects");

        edges.Should().ContainSingle(e =>
            e.GetProperty("from").GetString() == "server::moav" &&
            e.GetProperty("to").GetString() == "server::moav::SetSpeed" &&
            e.GetProperty("kind").GetString() == "uses");
    }

    [Fact]
    public void Build_NoServers_OnlyRootNodeAndNoEdges()
    {
        var root = BuildAsJson([]);
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        var edges = root.GetProperty("edges").EnumerateArray().ToList();

        nodes.Should().ContainSingle();
        nodes.Single().GetProperty("id").GetString().Should().Be("BrainAgent");
        edges.Should().BeEmpty();
    }

    [Fact]
    public void Build_MultipleServers_EachGetsItsOwnServerNode()
    {
        var groups = new List<(string ServerName, IReadOnlyList<AIFunction> Tools)>
        {
            ("moav", [FakeTool("ListFleet", "x")]),
            ("watchdog", [FakeTool("GetServicesHealth", "x")]),
            ("simulator", [FakeTool("ListSimulatorLessons", "x")])
        };

        var root = BuildAsJson(groups);
        var serverNodes = root.GetProperty("nodes").EnumerateArray()
            .Where(n => n.GetProperty("type").GetString() == "server")
            .Select(n => n.GetProperty("name").GetString())
            .ToList();

        serverNodes.Should().BeEquivalentTo(["moav", "watchdog", "simulator"]);
    }

    [Fact]
    public void Build_ToolParameters_ReflectsTheToolsOwnJsonSchema()
    {
        var groups = new List<(string ServerName, IReadOnlyList<AIFunction> Tools)>
        {
            ("moav", [FakeTool("SetSpeed", "x", tailNumberDescription: "the tail number")])
        };

        var root = BuildAsJson(groups);
        var toolNode = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "tool");
        var parameters = toolNode.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()).ToList();

        parameters.Should().Contain("tailNumber");
    }
}

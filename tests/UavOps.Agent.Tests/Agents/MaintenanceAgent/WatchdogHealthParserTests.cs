using FluentAssertions;
using UavOps.Agent.Agents.MaintenanceAgent;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class WatchdogHealthParserTests
{
    [Fact]
    public void Parse_StandardHealthChecksShape_ParsesOverallStatusAndEntries()
    {
        const string json = """
            {
              "status": "Degraded",
              "entries": {
                "telemetry-relay": { "status": "Healthy", "description": null },
                "payload-bridge": { "status": "Degraded", "description": "High response latency" }
              }
            }
            """;
        var polledAt = DateTimeOffset.UtcNow;

        var snapshot = WatchdogHealthParser.Parse(json, polledAt);

        snapshot.OverallStatus.Should().Be("Degraded");
        snapshot.PolledAt.Should().Be(polledAt);
        snapshot.Stale.Should().BeFalse();
        snapshot.Services.Should().HaveCount(2);
        snapshot.Services["telemetry-relay"].Should().Be(new ServiceHealthEntry("Healthy", null));
        snapshot.Services["payload-bridge"].Should().Be(new ServiceHealthEntry("Degraded", "High response latency"));
    }

    [Fact]
    public void Parse_CamelCasePropertyNames_StillResolves()
    {
        const string json = """{"status":"Healthy","entries":{"svc-a":{"status":"Healthy"}}}""";

        var snapshot = WatchdogHealthParser.Parse(json, DateTimeOffset.UtcNow);

        snapshot.OverallStatus.Should().Be("Healthy");
        snapshot.Services["svc-a"].Status.Should().Be("Healthy");
    }

    [Fact]
    public void Parse_NoEntries_ReturnsEmptyServicesDictionary()
    {
        const string json = """{"status":"Healthy"}""";

        var snapshot = WatchdogHealthParser.Parse(json, DateTimeOffset.UtcNow);

        snapshot.OverallStatus.Should().Be("Healthy");
        snapshot.Services.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        var act = () => WatchdogHealthParser.Parse("not json", DateTimeOffset.UtcNow);

        act.Should().Throw<System.Text.Json.JsonException>();
    }
}

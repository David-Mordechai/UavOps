using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi.Models;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class UavApiOperationToolTests
{
    private static UavApiOperationTool CreateSut(UavApiOperationDescriptor descriptor, AgentToolConfig config)
    {
        var invoker = new UavApiToolInvoker(new HttpClient());
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);

        return new UavApiOperationTool(descriptor, config, invoker, toolLogger, confirmationGate, "TestAgent", "corr1");
    }

    private static UavApiOperationDescriptor SetSpeedDescriptor() => new(
        "SetSpeed", OperationType.Post, "/uavs/{tailNumber}/speed",
        [
            new UavApiParameterDescriptor("tailNumber", ParamLocation.Path, true, new OpenApiSchema { Type = "string" }),
            new UavApiParameterDescriptor("speedKts", ParamLocation.BodyProperty, true, new OpenApiSchema { Type = "integer" })
        ]);

    [Fact]
    public void JsonSchema_IncludesConfiguredDescriptions_AndMarksParametersRequired()
    {
        var config = new AgentToolConfig
        {
            OperationId = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string>
            {
                ["tailNumber"] = "the tail number",
                ["speedKts"] = "the speed in knots"
            }
        };

        var sut = CreateSut(SetSpeedDescriptor(), config);
        var schema = sut.JsonSchema;

        var properties = schema.GetProperty("properties");
        properties.GetProperty("tailNumber").GetProperty("description").GetString().Should().Be("the tail number");
        properties.GetProperty("speedKts").GetProperty("description").GetString().Should().Be("the speed in knots");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain(["tailNumber", "speedKts"]);
    }

    [Fact]
    public void JsonSchema_ExcludesFixedParameters()
    {
        var config = new AgentToolConfig
        {
            OperationId = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string> { ["speedKts"] = "the speed in knots" },
            FixedParameters = new Dictionary<string, string> { ["tailNumber"] = "UAV-1" }
        };

        var sut = CreateSut(SetSpeedDescriptor(), config);
        var schema = sut.JsonSchema;

        var properties = schema.GetProperty("properties");
        properties.TryGetProperty("tailNumber", out _).Should().BeFalse();
        properties.TryGetProperty("speedKts", out _).Should().BeTrue();
    }

    [Fact]
    public void Name_IsTheOperationId()
    {
        var config = new AgentToolConfig { OperationId = "SetSpeed", Description = "Change speed" };

        var sut = CreateSut(SetSpeedDescriptor(), config);

        sut.Name.Should().Be("SetSpeed");
    }
}

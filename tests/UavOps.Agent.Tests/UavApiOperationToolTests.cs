using System.Collections.Generic;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
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

    /// <summary>
    /// Builds a real (non-networked) UavApiOperationTool wired to a capturing HTTP handler and a
    /// hub mock that reacts to the confirmation prompt exactly like ChatHub does — used by the
    /// RequiresConfirmation gating tests below, which need to exercise InvokeCoreAsync itself
    /// rather than just the JsonSchema it builds.
    /// </summary>
    private static (UavApiOperationTool Tool, CapturingHandler Handler) CreateSutWithConfirmation(
        UavApiOperationDescriptor descriptor, AgentToolConfig config, ExecutionMode mode, string? chatReply)
    {
        var handler = new CapturingHandler();
        var invoker = new UavApiToolInvoker(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5250") });

        ConfirmationGate? confirmationGate = null;
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (chatReply is not null)
                {
                    _ = confirmationGate!.TryHandleChatReplyAsync(chatReply, CancellationToken.None);
                }
                return Task.CompletedTask;
            });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);

        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ExecutionMode"] = mode.ToString() }).Build();
        confirmationGate = new ConfirmationGate(hub, configuration, NullLogger<ConfirmationGate>.Instance);

        var tool = new UavApiOperationTool(descriptor, config, invoker, toolLogger, confirmationGate, "TestAgent", "corr1");
        return (tool, handler);
    }

    private static UavApiOperationDescriptor ReturnToLaunchDescriptor() => new(
        "ReturnToLaunch", OperationType.Post, "/uavs/{tailNumber}/rtl",
        [new UavApiParameterDescriptor("tailNumber", ParamLocation.Path, true, new OpenApiSchema { Type = "string" })]);

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

    [Fact]
    public async Task InvokeCoreAsync_ToolNotMarkedRequiresConfirmation_ExecutesDirectly_EvenInConfirmMode()
    {
        // SetSpeed is a mutating (POST) operation but RequiresConfirmation defaults to false —
        // it must run immediately, with no confirmation prompt, even though the gate is in
        // Confirm mode. This is the "not all-or-nothing" behavior: gating is opt-in per tool,
        // not inferred from the HTTP verb.
        var config = new AgentToolConfig
        {
            OperationId = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail", ["speedKts"] = "speed" }
        };
        var (sut, handler) = CreateSutWithConfirmation(SetSpeedDescriptor(), config, ExecutionMode.Confirm, chatReply: null);

        await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1", ["speedKts"] = 200 }), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeCoreAsync_RequiresConfirmation_Declined_DoesNotCallHttp()
    {
        var config = new AgentToolConfig
        {
            OperationId = "ReturnToLaunch",
            Description = "Return to launch",
            RequiresConfirmation = true,
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail" }
        };
        var (sut, handler) = CreateSutWithConfirmation(ReturnToLaunchDescriptor(), config, ExecutionMode.Confirm, chatReply: "no");

        var result = await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        handler.LastRequest.Should().BeNull();
        result!.ToString().Should().Contain("Not executed");
    }

    [Fact]
    public async Task InvokeCoreAsync_RequiresConfirmation_Approved_CallsHttp()
    {
        var config = new AgentToolConfig
        {
            OperationId = "ReturnToLaunch",
            Description = "Return to launch",
            RequiresConfirmation = true,
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail" }
        };
        var (sut, handler) = CreateSutWithConfirmation(ReturnToLaunchDescriptor(), config, ExecutionMode.Confirm, chatReply: "yes");

        await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeCoreAsync_RequiresConfirmation_ButModeIsDirect_ExecutesDirectly()
    {
        // ExecutionMode is the global override: even a tool explicitly marked
        // RequiresConfirmation=true skips the prompt entirely when the gate is in Direct mode.
        var config = new AgentToolConfig
        {
            OperationId = "ReturnToLaunch",
            Description = "Return to launch",
            RequiresConfirmation = true,
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail" }
        };
        var (sut, handler) = CreateSutWithConfirmation(ReturnToLaunchDescriptor(), config, ExecutionMode.Direct, chatReply: null);

        await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
    }
}

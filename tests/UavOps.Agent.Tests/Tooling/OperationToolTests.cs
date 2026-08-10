using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Tooling;

public class OperationToolTests
{
    private static OperationDescriptor SetSpeedDescriptor() => new(
        "SetSpeed", typeof(IOperationService).GetMethod(nameof(IOperationService.SetSpeed))!,
        [
            new OperationParameterDescriptor("tailNumber", typeof(string)),
            new OperationParameterDescriptor("speedKts", typeof(int))
        ]);

    private static OperationDescriptor ReturnToLaunchDescriptor() => new(
        "ReturnToLaunch", typeof(IOperationService).GetMethod(nameof(IOperationService.ReturnToLaunch))!,
        [new OperationParameterDescriptor("tailNumber", typeof(string))]);

    private static OperationDescriptor UploadWaypointsDescriptor() => new(
        "UploadWaypoints", typeof(IOperationService).GetMethod(nameof(IOperationService.UploadWaypoints))!,
        [
            new OperationParameterDescriptor("tailNumber", typeof(string)),
            new OperationParameterDescriptor("waypoints", typeof(List<Waypoint>))
        ]);

    private static OperationDescriptor UpdateConfiguredServiceDescriptor() => new(
        "UpdateConfiguredService", typeof(IWatchdogConfigService).GetMethod(nameof(IWatchdogConfigService.UpdateConfiguredService))!,
        [
            new OperationParameterDescriptor("configurationName", typeof(string)),
            new OperationParameterDescriptor("description", typeof(string)),
            new OperationParameterDescriptor("newDescription", typeof(string)),
            new OperationParameterDescriptor("executable", typeof(string)),
            new OperationParameterDescriptor("args", typeof(List<string>)),
            new OperationParameterDescriptor("id", typeof(string)),
            new OperationParameterDescriptor("disabled", typeof(bool?)),
            new OperationParameterDescriptor("retries", typeof(int?)),
            new OperationParameterDescriptor("isManaged", typeof(bool?)),
            new OperationParameterDescriptor("healthEndPoint", typeof(string)),
            new OperationParameterDescriptor("group", typeof(string))
        ]);

    private static OperationTool CreateSut(OperationDescriptor descriptor, AgentToolConfig config, object operationService)
    {
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);

        return new OperationTool(descriptor, config, operationService, toolLogger, confirmationGate, "TestAgent", "corr1");
    }

    /// <summary>
    /// Builds a real OperationTool wired to a hub mock that reacts to the confirmation prompt
    /// exactly like ChatHub does — used by the RequiresConfirmation gating tests below, which
    /// need to exercise InvokeCoreAsync itself rather than just the JsonSchema it builds.
    /// </summary>
    private static OperationTool CreateSutWithConfirmation(
        OperationDescriptor descriptor, AgentToolConfig config, IOperationService operationService, ExecutionMode mode, string? chatReply)
    {
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

        return new OperationTool(descriptor, config, operationService, toolLogger, confirmationGate, "TestAgent", "corr1");
    }

    [Fact]
    public void JsonSchema_IncludesConfiguredDescriptions_AndMarksParametersRequired()
    {
        var config = new AgentToolConfig
        {
            Operation = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string>
            {
                ["tailNumber"] = "the tail number",
                ["speedKts"] = "the speed in knots"
            }
        };

        var sut = CreateSut(SetSpeedDescriptor(), config, Substitute.For<IOperationService>());
        var schema = sut.JsonSchema;

        var properties = schema.GetProperty("properties");
        properties.GetProperty("tailNumber").GetProperty("description").GetString().Should().Be("the tail number");
        properties.GetProperty("speedKts").GetProperty("description").GetString().Should().Be("the speed in knots");
        properties.GetProperty("speedKts").GetProperty("type").GetString().Should().Be("integer");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain(["tailNumber", "speedKts"]);
    }

    [Fact]
    public void JsonSchema_ExcludesFixedParameters()
    {
        var config = new AgentToolConfig
        {
            Operation = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string> { ["speedKts"] = "the speed in knots" },
            FixedParameters = new Dictionary<string, string> { ["tailNumber"] = "UAV-1" }
        };

        var sut = CreateSut(SetSpeedDescriptor(), config, Substitute.For<IOperationService>());
        var schema = sut.JsonSchema;

        var properties = schema.GetProperty("properties");
        properties.TryGetProperty("tailNumber", out _).Should().BeFalse();
        properties.TryGetProperty("speedKts", out _).Should().BeTrue();
    }

    [Fact]
    public void JsonSchema_NestedListParameter_BuildsArrayOfObjectSchema()
    {
        var config = new AgentToolConfig
        {
            Operation = "UploadWaypoints",
            Description = "Upload waypoints",
            Parameters = new Dictionary<string, string>
            {
                ["tailNumber"] = "the tail number",
                ["waypoints"] = "the waypoints"
            }
        };

        var sut = CreateSut(UploadWaypointsDescriptor(), config, Substitute.For<IOperationService>());
        var waypointsSchema = sut.JsonSchema.GetProperty("properties").GetProperty("waypoints");

        waypointsSchema.GetProperty("type").GetString().Should().Be("array");
        var itemProps = waypointsSchema.GetProperty("items").GetProperty("properties");
        itemProps.TryGetProperty("lat", out _).Should().BeTrue();
        itemProps.TryGetProperty("altitudeFt", out _).Should().BeTrue();
    }

    [Fact]
    public void JsonSchema_NullableValueTypeParameter_IsTypedAsItsUnderlyingType_AndNotRequired()
    {
        // Regression: bool?/int? parameters (e.g. IWatchdogConfigService.UpdateConfiguredService's
        // disabled/retries/isManaged) must NOT be reflected as a nested object describing
        // Nullable<T>'s own HasValue/Value shape — that previously produced a schema the model
        // dutifully filled in as {"hasValue":true,"value":true}, which then failed to deserialize
        // back into a real bool? and caused repeated failed tool-call retries in production.
        var config = new AgentToolConfig
        {
            Operation = "UpdateConfiguredService",
            Description = "Update a service",
            Parameters = new Dictionary<string, string>
            {
                ["configurationName"] = "config", ["description"] = "desc", ["newDescription"] = "new desc",
                ["executable"] = "exe", ["args"] = "args", ["id"] = "id",
                ["disabled"] = "disabled", ["retries"] = "retries", ["isManaged"] = "managed", ["healthEndPoint"] = "health"
            }
        };

        var sut = CreateSut(UpdateConfiguredServiceDescriptor(), config, Substitute.For<IWatchdogConfigService>());
        var properties = sut.JsonSchema.GetProperty("properties");

        properties.GetProperty("disabled").GetProperty("type").GetString().Should().Be("boolean");
        properties.GetProperty("isManaged").GetProperty("type").GetString().Should().Be("boolean");
        properties.GetProperty("retries").GetProperty("type").GetString().Should().Be("integer");
        properties.GetProperty("disabled").TryGetProperty("properties", out _).Should().BeFalse(); // not a nested HasValue/Value object

        var required = sut.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain(["configurationName", "description"]);
        required.Should().NotContain(["disabled", "retries", "isManaged"]);
    }

    [Fact]
    public async Task InvokeCoreAsync_ResultContainsYamlField_BuffersItForTheTurnsFinalReply()
    {
        // Regression: relying on the model to faithfully relay a returned "yaml" snippet in its
        // own final reply proved unreliable in production (same class of issue as the nullable
        // schema bug above) — the tool must surface it deterministically instead, mirroring
        // SimulatorLessonJobProcessor's proactive lesson-outcome notifications. Buffered (not sent
        // as its own chat message) so ChatHub can fold it into the turn's single final reply — see
        // ToolInvocationLogger.BufferProactiveMessage/TakeProactiveMessages.
        var config = new AgentToolConfig
        {
            Operation = "UpdateConfiguredService",
            Description = "Update a service",
            Parameters = new Dictionary<string, string> { ["configurationName"] = "c", ["description"] = "d" }
        };
        var watchdogConfigService = Substitute.For<IWatchdogConfigService>();
        watchdogConfigService.UpdateConfiguredService(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(),
            Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new { updated = "x", yaml = "- description: 'Test Service'\n  executable: 'x.exe'" })));

        var hub = Substitute.For<IHubContext<ChatHub>>();
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);
        var sut = new OperationTool(UpdateConfiguredServiceDescriptor(), config, watchdogConfigService, toolLogger, confirmationGate, "WatchdogConfigAgent", "corr1");

        await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["configurationName"] = "Flight", ["description"] = "Test Service" }),
            CancellationToken.None);

        var buffered = toolLogger.TakeProactiveMessages("corr1");
        buffered.Should().ContainSingle();
        buffered[0].Should().Contain("```yaml").And.Contain("- description: 'Test Service'");
    }

    [Fact]
    public void Name_IsTheOperation()
    {
        var config = new AgentToolConfig { Operation = "SetSpeed", Description = "Change speed" };

        var sut = CreateSut(SetSpeedDescriptor(), config, Substitute.For<IOperationService>());

        sut.Name.Should().Be("SetSpeed");
    }

    [Fact]
    public async Task InvokeCoreAsync_Success_ReturnsSerializedValue()
    {
        var config = new AgentToolConfig
        {
            Operation = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail", ["speedKts"] = "speed" }
        };
        var operationService = Substitute.For<IOperationService>();
        operationService.SetSpeed("UAV-1", 200, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new TelemetrySnapshot(1, 2, 200, 4000, "Orbiting", null))));
        var sut = CreateSut(SetSpeedDescriptor(), config, operationService);

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1", ["speedKts"] = 200 }), CancellationToken.None);

        result!.ToString().Should().Contain("\"speedKts\":200");
    }

    [Fact]
    public async Task InvokeCoreAsync_Failure_ReturnsErrorText()
    {
        var config = new AgentToolConfig
        {
            Operation = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail", ["speedKts"] = "speed" }
        };
        var operationService = Substitute.For<IOperationService>();
        operationService.SetSpeed(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.NotFound("UAV-99")));
        var sut = CreateSut(SetSpeedDescriptor(), config, operationService);

        var result = await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-99", ["speedKts"] = 200 }), CancellationToken.None);

        result!.ToString().Should().Contain("Error:").And.Contain("UAV-99");
    }

    [Fact]
    public async Task InvokeCoreAsync_ToolNotMarkedRequiresConfirmation_ExecutesDirectly_EvenInConfirmMode()
    {
        // SetSpeed is a mutating operation but RequiresConfirmation defaults to false — it must
        // run immediately, with no confirmation prompt, even though the gate is in Confirm mode.
        // Gating is opt-in per tool, never inferred.
        var config = new AgentToolConfig
        {
            Operation = "SetSpeed",
            Description = "Change speed",
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail", ["speedKts"] = "speed" }
        };
        var operationService = Substitute.For<IOperationService>();
        operationService.SetSpeed(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new TelemetrySnapshot(0, 0, 0, 0, "Orbiting", null))));
        var sut = CreateSutWithConfirmation(SetSpeedDescriptor(), config, operationService, ExecutionMode.Confirm, chatReply: null);

        await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1", ["speedKts"] = 200 }), CancellationToken.None);

        await operationService.Received(1).SetSpeed("UAV-1", 200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeCoreAsync_RequiresConfirmation_Declined_DoesNotCallOperationService()
    {
        var config = new AgentToolConfig
        {
            Operation = "ReturnToLaunch",
            Description = "Return to launch",
            RequiresConfirmation = true,
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail" }
        };
        var operationService = Substitute.For<IOperationService>();
        var sut = CreateSutWithConfirmation(ReturnToLaunchDescriptor(), config, operationService, ExecutionMode.Confirm, chatReply: "no");

        var result = await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        await operationService.DidNotReceive().ReturnToLaunch(Arg.Any<string>(), Arg.Any<CancellationToken>());
        result!.ToString().Should().Contain("Not executed");
    }

    [Fact]
    public async Task InvokeCoreAsync_RequiresConfirmation_Approved_CallsOperationService()
    {
        var config = new AgentToolConfig
        {
            Operation = "ReturnToLaunch",
            Description = "Return to launch",
            RequiresConfirmation = true,
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail" }
        };
        var operationService = Substitute.For<IOperationService>();
        operationService.ReturnToLaunch(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new TelemetrySnapshot(0, 0, 0, 0, "ReturningToLaunch", null))));
        var sut = CreateSutWithConfirmation(ReturnToLaunchDescriptor(), config, operationService, ExecutionMode.Confirm, chatReply: "yes");

        await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        await operationService.Received(1).ReturnToLaunch("UAV-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeCoreAsync_RequiresConfirmation_ButModeIsDirect_ExecutesDirectly()
    {
        // ExecutionMode is the global override: even a tool explicitly marked
        // RequiresConfirmation=true skips the prompt entirely when the gate is in Direct mode.
        var config = new AgentToolConfig
        {
            Operation = "ReturnToLaunch",
            Description = "Return to launch",
            RequiresConfirmation = true,
            Parameters = new Dictionary<string, string> { ["tailNumber"] = "tail" }
        };
        var operationService = Substitute.For<IOperationService>();
        operationService.ReturnToLaunch(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new TelemetrySnapshot(0, 0, 0, 0, "ReturningToLaunch", null))));
        var sut = CreateSutWithConfirmation(ReturnToLaunchDescriptor(), config, operationService, ExecutionMode.Direct, chatReply: null);

        await sut.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }), CancellationToken.None);

        await operationService.Received(1).ReturnToLaunch("UAV-1", Arg.Any<CancellationToken>());
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MoavAgent.Operations;

/// <summary>
/// Replaces the old <c>RemoteOperationServiceTests</c> (that class deleted along with
/// <c>RemoteOperationService</c> itself) - verifies <see cref="ChatHub"/>'s <c>Relay*</c> methods
/// (see its own doc comment), the direct successor: each one still calls the right typed
/// <see cref="IOperationClientProxy"/> method with the right arguments via
/// <see cref="IRemoteOperationBroker.SendAsync{TResult}"/>, and still formats the broker's
/// <see cref="OperationResult"/> the same camelCase-JSON-or-"Error: ..." way <c>Tooling/
/// OperationTool.cs</c> always did - only now producing a plain string for
/// <c>UavOps.Agent.McpMoav</c>'s own SignalR client to consume directly, instead of being an
/// <see cref="IOperationService"/> implementation resolved in-process.
/// </summary>
public class ChatHubRelayTests
{
    private static (ChatHub Hub, OperatorPromptGate OperatorPromptGate) CreateSut(IRemoteOperationBroker broker)
    {
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);
        var operatorPromptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance);
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);

        // MainAgentOrchestrator/AgentFactory are never touched by any Relay* method (only
        // PushLessonOutcome touches AgentFactory) - null! is fine here since they're only
        // constructor dependencies of ChatHub, never invoked by the methods under test.
        var chatHub = new ChatHub(null!, confirmationGate, operatorPromptGate, toolLogger, broker, hub, null!, NullLogger<ChatHub>.Instance);
        return (chatHub, operatorPromptGate);
    }

    [Fact]
    public async Task RelayGetTelemetry_BrokerSucceeds_ReturnsCamelCaseJson()
    {
        var broker = Substitute.For<IRemoteOperationBroker>();
        var snapshot = new TelemetrySnapshot(31.8, 34.6, 120, 4000, "Orbiting", null);
        broker.SendAsync<TelemetrySnapshot>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(snapshot)));
        var (sut, _) = CreateSut(broker);

        var result = await sut.RelayGetTelemetry("UAV-1");

        result.Should().Contain("\"speedKts\":120").And.Contain("\"mode\":\"Orbiting\"");
    }

    [Fact]
    public async Task RelayGetTelemetry_BrokerFails_ReturnsErrorPrefixedText()
    {
        var broker = Substitute.For<IRemoteOperationBroker>();
        broker.SendAsync<TelemetrySnapshot>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Fail(OperationError.NoClientConnected, "No Moav command client is connected.")));
        var (sut, _) = CreateSut(broker);

        var result = await sut.RelayGetTelemetry("UAV-1");

        result.Should().Be("Error: No Moav command client is connected.");
    }

    [Fact]
    public async Task RelayNavigate_PassesTailNumberAndLocationToClientProxy()
    {
        // Proves the invoke lambda actually calls the right typed proxy method with the right
        // arguments - mocking only the broker's return value (as the other tests here do) can't
        // catch an args mix-up, since the lambda is never run.
        var broker = Substitute.For<IRemoteOperationBroker>();
        var proxy = Substitute.For<IOperationClientProxy>();
        Func<IOperationClientProxy, string, Task>? capturedInvoke = null;
        broker.SendAsync<TelemetrySnapshot>(
                Arg.Do<Func<IOperationClientProxy, string, Task>>(invoke => capturedInvoke = invoke),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(new TelemetrySnapshot(0, 0, 0, 0, "Transiting", null))));
        var (sut, _) = CreateSut(broker);

        await sut.RelayNavigate("UAV-1", "target alpha");

        capturedInvoke.Should().NotBeNull();
        _ = capturedInvoke!(proxy, "corr1");
        _ = proxy.Received(1).Navigate("corr1", "UAV-1", "target alpha");
    }

    [Fact]
    public async Task RelayListFleet_BrokerSucceeds_ReturnsValue()
    {
        var broker = Substitute.For<IRemoteOperationBroker>();
        var summaries = new List<UavSummary> { new("UAV-1", "Orbiting", 31.8, 34.6) };
        broker.SendAsync<List<UavSummary>>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(summaries)));
        var (sut, _) = CreateSut(broker);

        var result = await sut.RelayListFleet();

        result.Should().Contain("\"tailNumber\":\"UAV-1\"");
    }

    [Fact]
    public async Task RelayUploadWaypoints_BrokerSucceeds_ReturnsAcceptedCount()
    {
        var broker = Substitute.For<IRemoteOperationBroker>();
        broker.SendAsync<int>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Ok(3)));
        var (sut, _) = CreateSut(broker);

        var result = await sut.RelayUploadWaypoints("UAV-1", [new Waypoint(1, 2, 100)]);

        result.Should().Be("3");
    }

    [Fact]
    public async Task RelaySetTrackingMode_BrokerFails_ReturnsFailureWithRealReason()
    {
        var broker = Substitute.For<IRemoteOperationBroker>();
        broker.SendAsync<GdtLinkStatus>(Arg.Any<Func<IOperationClientProxy, string, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult.Fail(OperationError.Timeout, "Moav command client did not respond in time.")));
        var (sut, _) = CreateSut(broker);

        var result = await sut.RelaySetTrackingMode("UAV-1", "Manual");

        result.Should().Be("Error: Moav command client did not respond in time.");
    }

    // ----- Generic operator-prompt relay half -----

    [Fact]
    public async Task RelayAskOperatorChoice_OperatorRepliesWithAValidChoice_ReturnsIt()
    {
        var (sut, operatorPromptGate) = CreateSut(Substitute.For<IRemoteOperationBroker>());

        var relayTask = sut.RelayAskOperatorChoice("Which training lesson do you want to run?", ["intro-flight-basics.ps1", "advanced-navigation.ps1"]);

        // Same "poll until the gate has a pending prompt to answer" pattern the live tests use -
        // RelayAskOperatorChoice's own RequestChoiceAsync call opens the round-trip asynchronously.
        while (!await operatorPromptGate.TryHandleChatReplyAsync("advanced-navigation.ps1", CancellationToken.None))
        {
            await Task.Delay(10);
        }

        var result = await relayTask;

        result.Should().Be("advanced-navigation.ps1");
    }

    [Fact]
    public async Task RelayAskOperatorChoice_OperatorRepliesWithAOneBasedIndex_ReturnsMatchingChoice()
    {
        var (sut, operatorPromptGate) = CreateSut(Substitute.For<IRemoteOperationBroker>());

        var relayTask = sut.RelayAskOperatorChoice("Which training lesson do you want to run?", ["intro-flight-basics.ps1", "advanced-navigation.ps1"]);

        while (!await operatorPromptGate.TryHandleChatReplyAsync("2", CancellationToken.None))
        {
            await Task.Delay(10);
        }

        var result = await relayTask;

        result.Should().Be("advanced-navigation.ps1");
    }
}

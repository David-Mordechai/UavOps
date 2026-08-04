using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents;

public class DelegateAgentToolTests
{
    private sealed class EchoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "sub-agent reply")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task InvokeCoreAsync_LogsActualParentAgentName_NotHardcoded()
    {
        // Regression test: DelegateAgentTool used to hardcode "MainAgent" as the logged agentName
        // regardless of which agent actually built/owns the tool — wrong now that any agent
        // (e.g. MoavAgent) can hold delegate tools, not just the root.
        var subAgent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions { Name = "FlightControlAgent" });

        string? loggedAgentName = null;
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync("ReceiveAgentTrace", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<object?[]>(1); // correlationId, agent, tool, argsJson, result, durationSeconds
                loggedAgentName = (string?)args[1];
                return Task.CompletedTask;
            });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);

        var sut = new UavOps.Agent.Agents.DelegateAgentTool(
            "FlightControlAgent", "Handles flight.", subAgent, toolLogger, parentAgentName: "MoavAgent", correlationId: "corr1");

        await sut.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = "do something" }), CancellationToken.None);

        loggedAgentName.Should().Be("MoavAgent");
    }
}

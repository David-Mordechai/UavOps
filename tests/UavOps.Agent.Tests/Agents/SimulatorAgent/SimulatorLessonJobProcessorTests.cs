using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class SimulatorLessonJobProcessorTests
{
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The lesson finished successfully.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static AgentFactory CreateAgentFactory(IHubContext<ChatHub> hub)
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["SimulatorInfrastructureAgent"] = new AgentConfig { Instructions = "Summarize outcomes plainly.", Description = "Runs lessons." }
        };
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);
        var operatorPromptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance);
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f })])));
        var retrievalIndex = AgentRetrievalIndex.BuildAsync(agents, generator, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new AgentFactory(
            _ => new FakeChatClient(),
            "test-model",
            agents,
            new OperationCatalog(typeof(IOperationService)),
            Substitute.For<IOperationService>(),
            new OperationCatalog(typeof(ISimulatorService)),
            Substitute.For<ISimulatorService>(),
            new OperationCatalog(typeof(IWatchdogService)),
            Substitute.For<IWatchdogService>(),
            new OperationCatalog(typeof(IWatchdogConfigService)),
            Substitute.For<IWatchdogConfigService>(),
            retrievalIndex,
            new RetrievalOptions(),
            toolLogger,
            confirmationGate,
            operatorPromptGate,
            NullLogger<AgentFactory>.Instance);
    }

    private static (IHubContext<ChatHub> Hub, Func<Task<(string Agent, string Text, string CorrelationId)>> AwaitNextMessage) CreateHub()
    {
        var pending = new TaskCompletionSource<(string, string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync("ReceiveChatMessage", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<object?[]>(1); // agent, text, duration, correlationId
                pending.TrySetResult(((string)args[0]!, (string)args[1]!, (string)args[3]!));
                return Task.CompletedTask;
            });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<ChatHub>>();
        hub.Clients.Returns(clients);

        return (hub, async () => await pending.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProcessesJob_BuildsPersonaOnlyAgent_AndPushesReceiveChatMessage_WithFreshCorrelationId()
    {
        var (hub, awaitMessage) = CreateHub();
        var agentFactory = CreateAgentFactory(hub);
        var queue = new SimulatorLessonJobQueue();
        var executor = Substitute.For<ILessonExecutor>();
        executor.ExecuteAsync("lesson1.ps1", Arg.Any<CancellationToken>())
            .Returns((LessonOutcome.Succeeded, (string?)null));

        var sut = new SimulatorLessonJobProcessor(
            queue, executor, agentFactory, hub, NullLogger<SimulatorLessonJobProcessor>.Instance);
        await sut.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(new SimulatorLessonJob("lesson1.ps1", "original-corr", DateTimeOffset.UtcNow));

            var (agent, text, correlationId) = await awaitMessage();

            agent.Should().Be("SimulatorInfrastructureAgent");
            text.Should().Be("The lesson finished successfully.");
            correlationId.Should().NotBe("original-corr");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SecondJob_IsProcessedOnlyAfterFirstCompletes()
    {
        var (hub, awaitMessage) = CreateHub();
        var agentFactory = CreateAgentFactory(hub);
        var queue = new SimulatorLessonJobQueue();
        var firstGate = new TaskCompletionSource();
        var secondStarted = new TaskCompletionSource();
        var executor = Substitute.For<ILessonExecutor>();
        executor.ExecuteAsync("first.ps1", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await firstGate.Task; // held open until the test releases it
                return (LessonOutcome.Succeeded, (string?)null);
            });
        executor.ExecuteAsync("second.ps1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                secondStarted.TrySetResult();
                return Task.FromResult((LessonOutcome.Succeeded, (string?)null));
            });

        var sut = new SimulatorLessonJobProcessor(
            queue, executor, agentFactory, hub, NullLogger<SimulatorLessonJobProcessor>.Instance);
        await sut.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(new SimulatorLessonJob("first.ps1", "corr1", DateTimeOffset.UtcNow));
            queue.Enqueue(new SimulatorLessonJob("second.ps1", "corr2", DateTimeOffset.UtcNow));

            // The second job's executor call must not have started while the first is still held open.
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            secondStarted.Task.IsCompleted.Should().BeFalse();

            firstGate.SetResult();
            await awaitMessage(); // first job's completion message
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }
}

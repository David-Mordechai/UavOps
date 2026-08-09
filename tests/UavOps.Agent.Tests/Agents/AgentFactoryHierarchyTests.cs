using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Agents;

public class AgentFactoryHierarchyTests
{
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static AgentFactory CreateSut(Dictionary<string, AgentConfig> agents, AgentRetrievalIndex retrievalIndex)
    {
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);
        var operatorPromptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance);

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
            retrievalIndex,
            new RetrievalOptions(),
            toolLogger,
            confirmationGate,
            operatorPromptGate,
            NullLogger<AgentFactory>.Instance);
    }

    private static async Task<AgentRetrievalIndex> BuildFixedIndexAsync(Dictionary<string, AgentConfig> agents)
    {
        // Vector content is irrelevant here — every non-root agent below declares explicit
        // Children, so retrieval ranking never actually fires; this only needs to satisfy
        // AgentRetrievalIndex.BuildAsync's embedding call for each agent with a Description.
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f })])));

        return await AgentRetrievalIndex.BuildAsync(agents, generator, CancellationToken.None);
    }

    private static List<string> DelegateToolNames(AIAgent agent)
    {
        var options = (ChatClientAgentOptions)agent.GetService(typeof(ChatClientAgentOptions))!;
        return options.ChatOptions!.Tools!.OfType<AIFunction>().Select(f => f.Name).ToList();
    }

    [Theory]
    [InlineData("set UAV-1 speed to 200 knots")]
    [InlineData("start the simulator and run lesson 3")]
    [InlineData("what is the weather like today")]
    public async Task BuildMainAgentForTurn_AlwaysOffersExactlyItsDeclaredChildren_RegardlessOfOperatorText(string operatorText)
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "Route.", Children = ["MoavAgent", "SimulatorAgent"] },
            ["MoavAgent"] = new AgentConfig { Instructions = "Live ops.", Description = "Handles live UAV fleet operations.", Children = [] },
            ["SimulatorAgent"] = new AgentConfig { Instructions = "Sim ops.", Description = "Handles the training simulator." }
        };
        var retrievalIndex = await BuildFixedIndexAsync(agents);
        var sut = CreateSut(agents, retrievalIndex);

        var brainAgent = await sut.BuildMainAgentForTurn("corr1", operatorText, CancellationToken.None);

        DelegateToolNames(brainAgent).Should().BeEquivalentTo(["MoavAgent", "SimulatorAgent"]);
    }

    [Fact]
    public async Task BuildMainAgentForTurn_ExplicitEmptyChildren_IsALeaf_EvenThoughRetrievalWouldOfferCandidates()
    {
        // MoavAgent declares Children: [] explicitly, so it must build with zero delegate tools
        // even though "OtherAgent" exists and would otherwise be a retrieval candidate.
        // DelegateAgentTool doesn't expose its built sub-agent publicly, so this asserts
        // indirectly: build MoavAgent's own config as the *root* via a second factory, using the
        // same agent dictionary, and inspect its own delegate tools directly.
        var moavAgents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "Live ops.", Description = "Handles live UAV fleet operations.", Children = [] },
            ["OtherAgent"] = new AgentConfig { Instructions = "Other.", Description = "Some other unrelated specialist." }
        };
        var retrievalIndex = await BuildFixedIndexAsync(moavAgents);
        var sut = CreateSut(moavAgents, retrievalIndex);

        var moavAsRoot = await sut.BuildMainAgentForTurn("corr1", "anything", CancellationToken.None);

        DelegateToolNames(moavAsRoot).Should().BeEmpty();
    }
}

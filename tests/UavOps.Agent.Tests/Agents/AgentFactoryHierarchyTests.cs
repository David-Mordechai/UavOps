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
            (_, _) => new FakeChatClient(),
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
            new MemoryOptions(),
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

    private static List<string> DelegateToolNames(List<AITool> tools) =>
        tools.OfType<AIFunction>().Select(f => f.Name).ToList();

    [Theory]
    [InlineData("set UAV-1 speed to 200 knots")]
    [InlineData("start the simulator and run lesson 3")]
    [InlineData("what is the weather like today")]
    public async Task BuildRootToolsForTurn_CreatePlanCoversExactlyItsDeclaredChildren_RegardlessOfOperatorText(string operatorText)
    {
        // BrainAgent's own children are never independently-callable tools of their own - they're
        // internal steps CreatePlanTool executes (see CreatePlanTool's own doc comment) - so what
        // this test actually verifies is that CreatePlan's own schema lists exactly the explicit
        // Children, regardless of operator text (proving explicit Children beats retrieval ranking).
        var agents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "Route.", Children = ["MoavAgent", "SimulatorAgent"] },
            ["MoavAgent"] = new AgentConfig { Instructions = "Live ops.", Description = "Handles live UAV fleet operations.", Children = [] },
            ["SimulatorAgent"] = new AgentConfig { Instructions = "Sim ops.", Description = "Handles the training simulator." }
        };
        var retrievalIndex = await BuildFixedIndexAsync(agents);
        var sut = CreateSut(agents, retrievalIndex);

        var tools = await sut.BuildRootToolsForTurn("corr1", operatorText, CancellationToken.None);

        DelegateToolNames(tools).Should().BeEquivalentTo(["CreatePlan"]);
        var schemaText = tools.OfType<AIFunction>().Single().JsonSchema.GetRawText();
        schemaText.Should().Contain("MoavAgent").And.Contain("SimulatorAgent");
    }

    [Fact]
    public async Task BuildRootToolsForTurn_ExplicitEmptyChildren_CreatePlanHasNoAgents_EvenThoughRetrievalWouldOfferCandidates()
    {
        // BrainAgent declares Children: [] explicitly, so CreatePlan must end up with zero agents to
        // delegate to, even though "OtherAgent" exists and would otherwise be a retrieval candidate.
        var agents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "Route.", Description = "Routes.", Children = [] },
            ["OtherAgent"] = new AgentConfig { Instructions = "Other.", Description = "Some other unrelated specialist." }
        };
        var retrievalIndex = await BuildFixedIndexAsync(agents);
        var sut = CreateSut(agents, retrievalIndex);

        var tools = await sut.BuildRootToolsForTurn("corr1", "anything", CancellationToken.None);

        DelegateToolNames(tools).Should().BeEquivalentTo(["CreatePlan"]);
        var schemaText = tools.OfType<AIFunction>().Single().JsonSchema.GetRawText();
        schemaText.Should().NotContain("OtherAgent");
    }
}

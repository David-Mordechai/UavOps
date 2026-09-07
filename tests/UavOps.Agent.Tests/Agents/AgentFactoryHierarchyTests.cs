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

    private static AgentFactory CreateSut(AgentConfig config, int topK = 10)
    {
        var hub = Substitute.For<IHubContext<ChatHub>>();
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, hub);
        var confirmationGate = new ConfirmationGate(hub, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);
        var operatorPromptGate = new OperatorPromptGate(hub, NullLogger<OperatorPromptGate>.Instance);

        return new AgentFactory(
            (_, _) => new FakeChatClient(),
            "test-model",
            config,
            new OperationCatalog(typeof(IOperationService)),
            Substitute.For<IOperationService>(),
            new OperationCatalog(typeof(ISimulatorService)),
            Substitute.For<ISimulatorService>(),
            new OperationCatalog(typeof(IWatchdogService)),
            Substitute.For<IWatchdogService>(),
            new OperationCatalog(typeof(IWatchdogConfigService)),
            Substitute.For<IWatchdogConfigService>(),
            new RetrievalOptions { TopK = topK },
            new MemoryOptions(),
            toolLogger,
            confirmationGate,
            operatorPromptGate);
    }

    // Vector content is irrelevant to what these tests check (which NAMES survive retrieval, not
    // ranking quality - that's covered live by eval/tool-retrieval-lab) - a fake generator
    // returning an identical vector for every text just needs to satisfy
    // ToolRetrievalIndex.BuildAsync's batched embedding call.
    private static async Task<ToolRetrievalIndex> BuildFixedIndexAsync(IReadOnlyList<AITool> templateTools)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var count = callInfo.Arg<IEnumerable<string>>()!.Count();
                var embeddings = Enumerable.Range(0, count).Select(_ => new Embedding<float>(new float[] { 1f, 0f })).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            });

        return await ToolRetrievalIndex.BuildAsync(templateTools, generator, CancellationToken.None);
    }

    private static List<string> ToolNames(List<AITool> tools) =>
        tools.OfType<AIFunction>().Select(f => f.Name).ToList();

    [Fact]
    public void BuildTemplateTools_OneToolPerConfiguredOperation()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig { Operation = "ListFleet", Description = "x", ExampleUtterance = "x" },
                new AgentToolConfig { Operation = "GetServicesHealth", Description = "x", ExampleUtterance = "x" }
            ]
        };
        var sut = CreateSut(config);

        var tools = sut.BuildTemplateTools();

        ToolNames(tools).Should().BeEquivalentTo(["ListFleet", "GetServicesHealth"]);
    }

    [Fact]
    public async Task BuildToolsForTurn_EmptyTopK_ReturnsNoTools()
    {
        // No "safe no-op" tool appended anymore - tool_choice is never forced (see
        // MainAgentOrchestrator's own doc comment), so there's nothing that must always be present.
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools = [new AgentToolConfig { Operation = "ListFleet", Description = "x", ExampleUtterance = "x" }]
        };
        var sut = CreateSut(config, topK: 0);
        sut.RetrievalIndex = await BuildFixedIndexAsync(sut.BuildTemplateTools());

        var tools = await sut.BuildToolsForTurn("corr1", "hi there", CancellationToken.None);

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildToolsForTurn_NarrowsToTopK()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig { Operation = "ListFleet", Description = "x", ExampleUtterance = "x" },
                new AgentToolConfig { Operation = "GetServicesHealth", Description = "x", ExampleUtterance = "x" },
                new AgentToolConfig { Operation = "ListSimulatorLessons", Description = "x", ExampleUtterance = "x" }
            ]
        };
        var sut = CreateSut(config, topK: 1);
        sut.RetrievalIndex = await BuildFixedIndexAsync(sut.BuildTemplateTools());

        var tools = await sut.BuildToolsForTurn("corr1", "anything", CancellationToken.None);

        tools.Should().HaveCount(1);
    }
}

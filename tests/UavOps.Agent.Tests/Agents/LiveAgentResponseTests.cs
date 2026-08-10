using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OllamaSharp;
using UavOps.Agent.Agents;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Agents.MoavAgent.Simulation;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

public class LiveAgentResponseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GetLiveResponse()
    {
        var ollamaOptions = new OllamaOptions
        {
            Endpoint = "http://localhost:11434",
            DefaultModel = "granite4.1:3b",
            EmbeddingModel = "nomic-embed-text"
        };

        // Resolve absolute path to the AgentsConfig folder in the src project
        var currentDir = AppContext.BaseDirectory;
        var srcDir = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", "..", "src", "UavOps.Agent", "AgentsConfig"));
        var agentsConfig = AgentConfigLoader.LoadFromDirectory(srcDir);

        var catalog = new OperationCatalog(typeof(IOperationService));
        var simulatedService = new SimulatedUavOperationService();
        var simulatorInfraCatalog = new OperationCatalog(typeof(ISimulatorService));
        var simulatorInfraService = Substitute.For<ISimulatorService>();
        var watchdogCatalog = new OperationCatalog(typeof(IWatchdogService));
        var watchdogService = Substitute.For<IWatchdogService>();
        var watchdogConfigCatalog = new OperationCatalog(typeof(IWatchdogConfigService));
        var watchdogConfigService = Substitute.For<IWatchdogConfigService>();

        var embeddingGenerator = new OllamaApiClient(new Uri(ollamaOptions.Endpoint), ollamaOptions.EmbeddingModel);
        var retrievalIndex = await AgentRetrievalIndex.BuildAsync(agentsConfig, embeddingGenerator, CancellationToken.None);

        var retrievalOptions = new RetrievalOptions
        {
            MaxDelegatesPerAgent = 5,
            MaxDelegationDepth = 2
        };

        Func<string, IChatClient> chatClientFactory = modelName =>
        {
            var ollama = new OllamaApiClient(new Uri(ollamaOptions.Endpoint), modelName);
            return new FunctionInvokingChatClient(ollama) { AllowConcurrentInvocation = true };
        };

        var mockHubContext = Substitute.For<IHubContext<ChatHub>>();
        var mockConfig = Substitute.For<IConfiguration>();

        var factory = new AgentFactory(
            chatClientFactory,
            ollamaOptions.DefaultModel,
            agentsConfig,
            catalog,
            simulatedService,
            simulatorInfraCatalog,
            simulatorInfraService,
            watchdogCatalog,
            watchdogService,
            watchdogConfigCatalog,
            watchdogConfigService,
            retrievalIndex,
            retrievalOptions,
            new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, mockHubContext),
            new ConfirmationGate(mockHubContext, mockConfig, NullLogger<ConfirmationGate>.Instance, null),
            new OperatorPromptGate(mockHubContext, NullLogger<OperatorPromptGate>.Instance, null),
            NullLogger<AgentFactory>.Instance
        );

        var mainAgent = await factory.BuildMainAgentForTurn("corr-123", "set speed to 250 to uav 1", CancellationToken.None);
        var result = await mainAgent.RunAsync("set speed to 250 to uav 1");

        output.WriteLine("=== RESULT RESPONSE ===");
        output.WriteLine(result.Text);
        output.WriteLine("=======================");
    }
}

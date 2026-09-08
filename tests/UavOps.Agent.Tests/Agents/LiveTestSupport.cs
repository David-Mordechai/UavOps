using System.ClientModel;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Shared setup/helpers for every live (<c>Category=Live</c>) scenario test - split out of what
/// used to be one big <c>LiveAgentResponseTests</c> class specifically so each scenario can live in
/// its own test class. xUnit only parallelizes across separate test classes (each is its own
/// implicit "collection" by default) - methods within one class always run sequentially, no matter
/// what. With all 9 scenarios previously methods of one class, they always ran one after another
/// even though each is fully independent (its own fresh <see cref="BuildLiveOrchestrator"/> call,
/// no shared state) - splitting them into separate classes lets xUnit run them concurrently with no
/// other configuration needed, cutting real wall-clock time for the full live suite.
/// </summary>
internal static class LiveTestSupport
{
    /// <summary>
    /// Repeat count for every repeated scenario - defaults to 8 (this project's own established "a
    /// single pass proves nothing about reliability" standard), but overridable via the
    /// <c>LIVE_TEST_REPEATS</c> environment variable for fast day-to-day iteration
    /// (<c>LIVE_TEST_REPEATS=1 dotnet test ... --filter Category=Live</c> runs the full suite once
    /// each in a few minutes instead of the full confidence run) - added after a real incident where
    /// a full 8x run took over an hour with zero visible progress in between, making iterative
    /// development on this system impractical. Use the full default before considering a change
    /// actually validated; use 1-2 for a quick sanity check while actively iterating on a feature.
    /// </summary>
    public static readonly int RepeatCount =
        int.TryParse(Environment.GetEnvironmentVariable("LIVE_TEST_REPEATS"), out var n) && n > 0 ? n : 8;

    /// <summary>
    /// Real-time progress log, appended to directly (bypassing xUnit/VSTest's own output capture
    /// entirely) - confirmed empirically that neither <see cref="ITestOutputHelper.WriteLine(string)"/>
    /// nor plain <see cref="Console"/> output streams live through `dotnet test`: VSTest only
    /// reports progress at whole-test-CASE granularity (one line per finished
    /// <c>[Fact]</c>/<c>[Theory]</c>), batching everything written *during* a still-running test
    /// case regardless of which of those two channels it went through. Tail this file (or read it
    /// any time) while a live run is in progress to see genuine, current per-repeat progress instead
    /// of waiting for a whole test - or the whole run - to finish.
    /// </summary>
    public static readonly string ProgressLogPath = Path.Combine(Path.GetTempPath(), "uavops-live-test-progress.log");

    private static readonly object ProgressLogLock = new();
    private static bool _cleared;

    /// <summary>Clears any stale progress log from a previous run - called once by the first live
    /// test class to run in this process (see each class's own static constructor). Guarded by
    /// <see cref="ProgressLogLock"/> + a flag, not a single type's static constructor, because multiple
    /// independent test classes now exist and could otherwise race to clear the file out from
    /// under each other if xUnit starts them concurrently.</summary>
    public static void ClearProgressLogOnce()
    {
        lock (ProgressLogLock)
        {
            if (_cleared)
            {
                return;
            }

            File.WriteAllText(ProgressLogPath,
                $"=== live test run started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (LIVE_TEST_REPEATS={RepeatCount}) ==={Environment.NewLine}");
            _cleared = true;
        }
    }

    /// <summary>Real-time progress marker - see <see cref="ProgressLogPath"/>'s own doc comment for
    /// why this writes to a plain file instead of relying on <paramref name="output"/> alone.
    /// Shares <see cref="ProgressLogLock"/> with <see cref="ClearProgressLogOnce"/> - <see cref="File.AppendAllText(string, string)"/>
    /// opens the file exclusively for the duration of each call and does NOT tolerate concurrent
    /// writers on Windows; without this lock, splitting live tests into separate classes (so xUnit
    /// runs them in parallel) reliably threw <see cref="IOException"/> the moment two classes' first
    /// progress lines landed at the same instant - confirmed live (6 of 9 tests failed outright on
    /// this exact exception the first time these classes actually ran concurrently). A per-process
    /// in-memory lock is sufficient here (all writers are threads in the one test-host process, no
    /// separate process ever writes this file).</summary>
    public static void LiveLog(ITestOutputHelper output, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (ProgressLogLock)
        {
            File.AppendAllText(ProgressLogPath, line + Environment.NewLine);
        }
        output.WriteLine(message);
    }

    /// <summary>Disposing stops every child MCP server process in the group - lets every test's
    /// call site keep a single <c>await using var _ = ...;</c> regardless of how many servers
    /// <see cref="BuildLiveOrchestrator"/> connected to.</summary>
    public sealed class McpClientGroup(IReadOnlyList<McpClient> clients) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Builds an <see cref="AgentFactory"/> against whatever backend the real app is actually
    /// configured to use — reading <c>src/UavOps.Agent/appsettings.json</c> (plus user secrets, for
    /// <see cref="OpenAiOptions.ApiKey"/>) and applying the <c>AgentModels</c> overrides exactly like
    /// <c>Program.cs</c> does. This matters: these tests used to hardcode Ollama + granite4.1:3b,
    /// which is NOT what production actually talks to whenever <c>AgentModels</c> points agents at
    /// an OpenAI-compatible endpoint instead (as it does at the time of writing) - a fix validated
    /// against the wrong model proves nothing about the deployed behavior.
    /// </summary>
    public static async Task<(MainAgentOrchestrator Orchestrator, ToolInvocationLogger ToolLogger, Func<string, CancellationToken, Task<TelemetrySnapshot>> GetTelemetry, AgentFactory Factory, McpClientGroup McpClients, ConfirmationGate ConfirmationGate)> BuildLiveOrchestrator()
    {
        var currentDir = AppContext.BaseDirectory;
        var srcAgentDir = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", "..", "src", "UavOps.Agent"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(srcAgentDir)
            .AddJsonFile("appsettings.json")
            .AddUserSecrets("04b6ee4b-bc67-4479-bfa0-0e620ce3699f") // UavOps.Agent's <UserSecretsId>
            .Build();

        var ollamaOptions = configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>()
            ?? throw new InvalidOperationException("Missing Ollama configuration.");
        var openAiOptions = configuration.GetSection(OpenAiOptions.SectionName).Get<OpenAiOptions>() ?? new OpenAiOptions();
        var embeddingOptions = configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
            ?? throw new InvalidOperationException("Missing Embedding configuration.");

        var agentConfig = AgentConfigLoader.Load(Path.Combine(srcAgentDir, "Agents", "BrainAgent.yaml"));

        // Same "{configuration}" placeholder substitution UavOps.Agent's own Program.cs does - see
        // its own doc comment. This test project is always built (and run via `dotnet test`) in
        // whatever configuration this exact #if resolves to, matching the same-solution Mcp*
        // projects built alongside it via `dotnet build UavOps.sln` beforehand.
#if DEBUG
        const string buildConfiguration = "Debug";
#else
        const string buildConfiguration = "Release";
#endif
        foreach (var serverConfig in agentConfig.McpServers)
        {
            serverConfig.Args = serverConfig.Args.Select(a => a.Replace("{configuration}", buildConfiguration)).ToList();
        }

        // Same "apply AgentModels onto the loaded YAML" step Program.cs does, before anything else
        // touches agentConfig.
        var agentModelOptions = configuration.GetSection("AgentModels").Get<AgentModelOptions>();
        if (agentModelOptions is not null)
        {
            agentConfig.Provider = agentModelOptions.Provider;
            agentConfig.Model = agentModelOptions.Model;
        }

        var retrievalOptions = configuration.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>() ?? new RetrievalOptions();

        // Same provider branch as Program.cs's chatClientFactory registration.
        Func<string, string?, IChatClient> chatClientFactory = (modelName, provider) =>
        {
            IChatClient inner;
            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                var chatClient = new ChatClient(modelName, new ApiKeyCredential(openAiOptions.ApiKey ?? ""),
                    new OpenAIClientOptions { Endpoint = new Uri(openAiOptions.Endpoint) });
                inner = chatClient.AsIChatClient();
            }
            else
            {
                inner = new OllamaApiClient(new Uri(ollamaOptions.Endpoint), modelName);
            }
            return new FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true };
        };

        var mockHubContext = Substitute.For<IHubContext<ChatHub>>();
        var mockConfig = Substitute.For<IConfiguration>();
        var toolLogger = new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, mockHubContext);
        // Short timeout (not the 60s production default) - tests that never approve the request
        // (e.g. Greeting_NeverTriggersASpuriousRealOperation, which never touches a confirmation-
        // gated tool at all) are unaffected, and tests that DO approve it do so almost immediately
        // by calling TryHandleChatReplyAsync directly (see ConfirmationGate below) rather than
        // waiting out any timeout at all.
        var confirmationGate = new ConfirmationGate(mockHubContext, mockConfig, NullLogger<ConfirmationGate>.Instance, TimeSpan.FromSeconds(20));

        var factory = new AgentFactory(
            chatClientFactory,
            ollamaOptions.DefaultModel,
            agentConfig,
            retrievalOptions,
            new MemoryOptions(),
            toolLogger,
            confirmationGate,
            new OperatorPromptGate(mockHubContext, NullLogger<OperatorPromptGate>.Instance, null)
        );

        // Every configured MCP server (Moav, watchdog, simulator) - a brand-new child process per
        // call, same as production connects to them, so each repeat starts from truly fresh
        // in-memory state (a new process, not just a new C# instance). The caller is responsible
        // for disposing McpClients (stops every child process) once done with this
        // orchestrator/repeat.
        var mcpClients = new List<McpClient>();
        var mcpTools = new List<AIFunction>();
        foreach (var serverConfig in agentConfig.McpServers)
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = serverConfig.Name,
                Command = serverConfig.Command,
                Arguments = serverConfig.Args,
                // Same as Program.cs: args are authored relative to src/UavOps.Agent itself, so
                // resolve them the same way regardless of dotnet test's own working directory.
                WorkingDirectory = srcAgentDir
            });
            var client = await McpClient.CreateAsync(transport);
            mcpClients.Add(client);
            mcpTools.AddRange(await client.ListToolsAsync());
        }
        factory.McpTools = mcpTools;

        // Same as Program.cs: fold each connected server's own ServerInstructions into BrainAgent's
        // system prompt, so this test exercises the exact same effective instructions production
        // does, not just the YAML's cross-cutting subset.
        var domainInstructions = mcpClients
            .Select(c => c.ServerInstructions)
            .Where(instructions => !string.IsNullOrWhiteSpace(instructions));
        agentConfig.Instructions = string.Join("\n\n", [agentConfig.Instructions, .. domainInstructions]);

        async Task<TelemetrySnapshot> GetTelemetryAsync(string tailNumber, CancellationToken cancellationToken)
        {
            var tool = mcpTools.First(t => t.Name == "GetTelemetry");
            var raw = await tool.InvokeAsync(new AIFunctionArguments { ["tailNumber"] = tailNumber }, cancellationToken);
            var json = raw?.ToString() ?? throw new InvalidOperationException("GetTelemetry returned no result.");
            return JsonSerializer.Deserialize<TelemetrySnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"Could not parse GetTelemetry result: {json}");
        }

        // Real semantic embeddings - load-bearing for tool-call correctness now, same reasoning
        // Program.cs's own startup wiring documents (see ToolRetrievalIndex's own doc comment).
        var embeddingClient = new EmbeddingClient(embeddingOptions.Model, new ApiKeyCredential("not-needed"),
            new OpenAIClientOptions { Endpoint = new Uri(embeddingOptions.Endpoint) });
        var embeddingGenerator = embeddingClient.AsIEmbeddingGenerator();
        factory.RetrievalIndex = await ToolRetrievalIndex.BuildAsync(factory.BuildTemplateTools(), embeddingGenerator, CancellationToken.None);

        // MainAgentOrchestrator and AgentFactory are both singletons in production (Program.cs) -
        // reusing the same instances across the two HandleAsync calls below reproduces the
        // persistent-session lifetime that let the fabrication bug happen for real.
        return (new MainAgentOrchestrator(factory, toolLogger), toolLogger, GetTelemetryAsync, factory, new McpClientGroup(mcpClients), confirmationGate);
    }

    /// <summary>
    /// Repeatedly offers "yes" to <see cref="ConfirmationGate.TryHandleChatReplyAsync"/> until it
    /// is consumed by a pending confirmation, or <paramref name="guard"/> completes without ever
    /// requesting one. Needed because the approval round-trip is, in production, a SEPARATE
    /// incoming chat message handled by <c>ChatHub.SendMessage</c> before
    /// <see cref="ConfirmationGate.TryHandleChatReplyAsync"/> is checked - these orchestrator-level
    /// tests call <see cref="MainAgentOrchestrator.HandleAsync"/> directly with no real Hub in the
    /// loop, so nothing else here would ever answer a pending confirmation.
    /// </summary>
    public static async Task ApproveAnyPendingConfirmationAsync(ConfirmationGate gate, Task guard)
    {
        while (!guard.IsCompleted)
        {
            if (await gate.TryHandleChatReplyAsync("yes", CancellationToken.None))
            {
                return;
            }
            await Task.Delay(50);
        }
    }
}

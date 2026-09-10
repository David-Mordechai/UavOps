using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Serilog;
using System.ClientModel;
using UavOps.Agent.Agents;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

var builder = WebApplication.CreateBuilder(args);

// Operator-saved Settings-page overrides (see Options/SettingsStore.cs) - never the checked-in
// appsettings.json. Loaded last so it has final precedence; reloadOnChange means a save's new
// McpServersEnabled value is visible to a live IConfiguration read (see
// Options/McpServerSelection.cs) on the very next access, no restart needed for that one setting.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// File sink alongside the console one so tool-call/agent-reasoning behavior (e.g. the
// BrainAgent verified-retry logging in MainAgentOrchestrator) can be inspected after the fact
// without needing to have been watching the console at the time - added after a live incident
// where diagnosing a suspected fabrication required the operator to manually copy the chat UI's
// reasoning panel, since nothing was persisted anywhere else.
builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .WriteTo.File("logs/uavops-agent-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

var ollamaOptions = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>()
    ?? throw new InvalidOperationException($"Missing '{OllamaOptions.SectionName}' configuration section.");

var remoteOperationOptions = builder.Configuration.GetSection(RemoteOperationOptions.SectionName).Get<RemoteOperationOptions>()
    ?? new RemoteOperationOptions();

var openAiOptions = builder.Configuration.GetSection(OpenAiOptions.SectionName).Get<OpenAiOptions>()
    ?? new OpenAiOptions();

var embeddingOptions = builder.Configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
    ?? throw new InvalidOperationException($"Missing '{EmbeddingOptions.SectionName}' configuration section.");

var agentConfig = AgentConfigLoader.Load(Path.Combine(builder.Environment.ContentRootPath, "Agents", "BrainAgent.yaml"));

// Two distinct ways an MCP server's real dll location gets resolved, for two distinct scenarios
// (deliberately not one shared mechanism - a dev build's output and a real deployment's published
// output aren't just "Debug vs Release", they're different directory *shapes* entirely - `dotnet
// publish` doesn't produce a bin/<Configuration>/net8.0/ tree at all, confirmed directly rather
// than assumed after an earlier version of this comment got that wrong):
//
// 1. Dev/debug default (Visual Studio F5, `dotnet run`, or `dotnet build` + run in place from this
//    git checkout): BrainAgent.yaml's own args reference a sibling project's normal build output
//    ("../UavOps.Agent.McpMoav/bin/{configuration}/net8.0/...") - "{configuration}" is a
//    placeholder substituted below with whatever configuration THIS host binary was itself
//    compiled as (#if DEBUG/#else, the same compile-time constant the -c Debug/-c Release flag
//    that built this exact binary also used) - so it always points at the real, matching sibling
//    build without touching BrainAgent.yaml. The 3 Mcp* projects are explicit Visual Studio
//    "Project Dependencies" of this one in UavOps.sln (not a compile-time ProjectReference, since
//    this host never calls their code directly - only spawns them as processes), specifically so
//    F5-debugging this project in VS also rebuilds them first.
// 2. Deploy override (a real single-machine deployment running `dotnet publish` output, wherever
//    that landed): McpServerPaths:<name> in appsettings.json - left blank ("") by default, since
//    plain .json doesn't support comments to explain that inline - is checked FIRST per server;
//    when set (via appsettings.Production.json or an McpServerPaths__<name> environment variable,
//    set once by whatever deploys this app, never by hand-editing BrainAgent.yaml) it replaces
//    that server's dll path outright, however different that published layout is from the
//    dev-mode bin/ tree.
#if DEBUG
const string buildConfiguration = "Debug";
#else
const string buildConfiguration = "Release";
#endif
foreach (var serverConfig in agentConfig.McpServers)
{
    var deployedPath = builder.Configuration[$"McpServerPaths:{serverConfig.Name}"];
    serverConfig.Args = !string.IsNullOrWhiteSpace(deployedPath)
        ? ["exec", deployedPath]
        : serverConfig.Args.Select(a => a.Replace("{configuration}", buildConfiguration)).ToList();
}

// Single place to see/change which backend+model BrainAgent uses, instead of that being buried
// in Agents/BrainAgent.yaml (which stays focused on behavior/content). Applied directly onto
// the already-loaded AgentConfig, before validation runs so it sees the final provider/model it
// will actually use.
var agentModelOptions = builder.Configuration.GetSection("AgentModels").Get<AgentModelOptions>();
if (agentModelOptions is not null)
{
    agentConfig.Provider = agentModelOptions.Provider;
    agentConfig.Model = agentModelOptions.Model;
}

// Validates BrainAgent's own config fields before the app is allowed to start - every real
// operation across every domain is an MCP tool now, nothing configured in-process. Moav,
// watchdog, and simulator all moved to MCP (AgentConfig.McpServers) - their own startup check is
// simply whether connecting to each and listing its tools succeeds, below.
AgentConfigValidator.Validate(agentConfig);

var retrievalOptions = builder.Configuration.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>()
    ?? new RetrievalOptions();

var memoryOptions = builder.Configuration.GetSection(MemoryOptions.SectionName).Get<MemoryOptions>()
    ?? new MemoryOptions();

builder.Services.AddSingleton(ollamaOptions);
builder.Services.AddSingleton(openAiOptions);
builder.Services.AddSingleton(embeddingOptions);
builder.Services.AddSingleton(retrievalOptions);
builder.Services.AddSingleton(memoryOptions);
builder.Services.AddSingleton(remoteOperationOptions);
builder.Services.AddSingleton(agentConfig);
builder.Services.AddSingleton<SettingsStore>();

// Same singleton registered both ways so the MCP connection loop below (which runs after
// app.StartAsync()) can populate its Clients list, and the Generic Host separately treats it as
// this app's one IHostedService to stop - see McpClientsLifetimeService's own doc comment for why
// this replaced a synchronous ApplicationStopping.Register callback.
builder.Services.AddSingleton<McpClientsLifetimeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<McpClientsLifetimeService>());

// A confirmation reply (or an operation's SubmitCommandResult reply) is just another call on
// the same connection while the original call is still in flight, so raise the per-connection
// parallel-invocation limit above SignalR's default of 1 (which would otherwise queue the reply
// behind the in-progress call until it times out). Covers ChatHub's own two client roles (the
// browser SPA at /chatHub, the real Moav-commanding client at /uavCommandHub - see ChatHub's own
// doc comment for why both live on one Hub class).
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 10);

// Always registered — a Moav command client can connect at any time without an app restart.
// Backs both ChatHub's connection tracking and its Relay* methods, which UavOps.Agent.McpMoav's
// own SignalR client calls into under its own OperationBackend: SignalR - see ChatHub's own doc
// comment. RemoteOperationOptions (the broker's own reply timeout) stays here, not in McpMoav,
// since the broker itself stays here.
builder.Services.AddSingleton<IRemoteOperationBroker, RemoteOperationBroker>();

builder.Services.AddSingleton<ToolInvocationLogger>();
builder.Services.AddSingleton<ConfirmationGate>();
builder.Services.AddSingleton<OperatorPromptGate>();

builder.Services.AddSingleton<Func<string, string?, IChatClient>>(sp => (modelName, provider) =>
{
    IChatClient inner;
    if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        // ApiKey is optional, same reasoning as the embedding client below — a self-hosted
        // OpenAI-compatible server (vLLM, llama.cpp) doesn't check it at all, only a real
        // OpenAI/OpenRouter-style endpoint does, and ApiKeyCredential itself requires a non-empty
        // string regardless.
        var options = sp.GetRequiredService<OpenAiOptions>();
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? "not-needed" : options.ApiKey;
        var chatClient = new ChatClient(modelName, new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
        inner = chatClient.AsIChatClient();
    }
    else
    {
        var options = sp.GetRequiredService<OllamaOptions>();
        inner = new OllamaApiClient(new Uri(options.Endpoint), modelName);
    }
    return new FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true };
});

builder.Services.AddSingleton<AgentFactory>(sp =>
    new AgentFactory(
        sp.GetRequiredService<Func<string, string?, IChatClient>>(),
        ollamaOptions.DefaultModel,
        agentConfig,
        sp.GetRequiredService<RetrievalOptions>(),
        sp.GetRequiredService<MemoryOptions>(),
        sp.GetRequiredService<ToolInvocationLogger>(),
        sp.GetRequiredService<ConfirmationGate>(),
        sp.GetRequiredService<OperatorPromptGate>(),
        sp.GetRequiredService<IConfiguration>()
    ));

builder.Services.AddSingleton<MainAgentOrchestrator>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// One Hub class, two endpoints - the browser SPA and the real Moav-commanding client are two
// different kinds of client of the same ChatHub (see its own doc comment); neither's connection
// URL changes by mapping both here.
app.MapHub<ChatHub>("/chatHub");
app.MapHub<ChatHub>("/uavCommandHub");

app.MapGet("/healthz", () =>
{
    // This endpoint is reachable as soon as Kestrel starts listening (app.StartAsync() above),
    // which is now deliberately *before* the MCP connection loop and retrieval-index build below
    // finish - so RetrievalIndex may still be its unset `null!` default for the first moment or
    // two of the process's life. Report "starting" rather than let that surface as a 500.
    var factory = app.Services.GetRequiredService<AgentFactory>();
    if (factory.RetrievalIndex is null)
    {
        return Results.Ok(new { status = "starting" });
    }

    return Results.Ok(new
    {
        status = "ok",
        ollamaModel = ollamaOptions.DefaultModel,
        mcpServers = agentConfig.McpServers.Count,
        mcpTools = factory.McpTools.Count,
        retrievalTools = factory.RetrievalIndex.Count
    });
});

app.MapGet("/api/agent-graph", () =>
{
    var enabledServers = McpServerSelection.GetEnabledServerNames(
        app.Services.GetRequiredService<IConfiguration>(), agentConfig.McpServers.Select(s => s.Name));
    return Results.Ok(AgentGraphProjector.Build(app.Services.GetRequiredService<AgentFactory>().McpServerToolGroups, enabledServers));
});

app.MapGet("/api/settings", async () =>
    Results.Ok(await app.Services.GetRequiredService<SettingsStore>().GetEffectiveSettingsAsync()));

app.MapPost("/api/settings", async (System.Text.Json.Nodes.JsonObject body) =>
{
    await app.Services.GetRequiredService<SettingsStore>().SaveAsync(body);
    return Results.Ok();
});

app.MapGet("/api/validate-path", (string path) =>
    Results.Ok(new { exists = File.Exists(path) || Directory.Exists(path) }));

// Gives the Settings page's "Shut down now" button something to call after a save that needs a
// restart - this app has no self-relaunch/supervisor mechanism (see CLAUDE.md's "Running it"), so
// the operator relaunches it themselves right after. The short delay lets this response flush
// before Kestrel actually stops.
app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(300);
        lifetime.StopApplication();
    });
    return Results.Ok();
});

// Starts Kestrel actually listening now, with every endpoint/middleware above already registered
// (they must be mapped before this - late-mapped endpoints aren't guaranteed to take effect once
// the pipeline starts serving) - app.Run() below would otherwise defer listening until after the
// MCP connection loop, which is too late: UavOps.Agent.McpMoav's own SignalR client (under
// OperationBackend: SignalR) dials back into this same process's /uavCommandHub at ITS startup.
// Live-reproduced: without this, that dial-back failed with a connection error because nothing
// was listening yet, which in turn failed the whole MCP connection loop below and crashed startup
// entirely.
await app.StartAsync();

var agentFactory = app.Services.GetRequiredService<AgentFactory>();

// Connects to every configured MCP server (stdio, spawned as a child process of this one - see
// AgentConfig.McpServers's own doc comment) and discovers its tools, before anything else needs
// them - AgentFactory.BuildTemplateTools() (right below) reads McpTools, and a turn's own
// BuildToolsForTurn assumes it's already populated. Fail-fast at boot, same posture as the
// embedding endpoint below: an MCP server that won't start/connect must stop the app from coming
// up, not silently leave that domain's tools missing from every turn.
var mcpClients = new List<McpClient>();
var mcpTools = new List<AIFunction>();
var mcpServerToolGroups = new List<(string ServerName, IReadOnlyList<AIFunction> Tools)>();
foreach (var serverConfig in agentConfig.McpServers)
{
    try
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = serverConfig.Name,
            Command = serverConfig.Command,
            Arguments = serverConfig.Args,
            // How long to wait for this server's own process to exit cleanly before forcibly
            // killing it (StdioClientTransportOptions' own real purpose - not a workaround).
            // Root-caused, not guessed: confirmed via the MCP C# SDK's own source
            // (StreamServerTransport.ReadMessagesAsync) that hitting EOF on stdin only calls
            // SetDisconnected() - it never stops the hosting process itself, and the SDK's own
            // official QuickstartWeatherServer sample doesn't wire up self-termination either, so
            // this server never exits on its own; live-reproduced across all 3 servers that the
            // default 5s value was hit in full, every time, before this - a short, deliberate
            // value is the correct, SDK-intended way to control that wait, safe here since a
            // killed child is just a stateless local JSON-RPC listener loop with nothing to flush.
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            // args like "../UavOps.Agent.McpMoav/..." are authored relative to this project's own
            // directory (see Agents/BrainAgent.yaml) - explicit so they resolve the same way
            // regardless of the launching process's own working directory (e.g. under `dotnet test`).
            WorkingDirectory = builder.Environment.ContentRootPath
        });
        var client = await McpClient.CreateAsync(transport);
        mcpClients.Add(client);
        var tools = (await client.ListToolsAsync()).Cast<AIFunction>().ToList();
        mcpTools.AddRange(tools);
        mcpServerToolGroups.Add((serverConfig.Name, (IReadOnlyList<AIFunction>)tools));
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Failed to connect to MCP server '{serverConfig.Name}' ({serverConfig.Command} {string.Join(' ', serverConfig.Args)}). " +
            $"Please ensure it can start correctly. {ex.Message}", ex);
    }
}
agentFactory.McpTools = mcpTools;
agentFactory.McpServerToolGroups = mcpServerToolGroups;
app.Services.GetRequiredService<McpClientsLifetimeService>().Clients.AddRange(mcpClients);

// Every discovered tool's owning server, for ToolRetrievalIndex to tag each embedded entry with -
// see its own doc comment for why every tool is always indexed regardless of that server's
// enabled/disabled state.
var toolNameToServerName = mcpServerToolGroups
    .SelectMany(group => group.Tools.Select(tool => (tool.Name, group.ServerName)))
    .ToDictionary(x => x.Name, x => x.ServerName, StringComparer.Ordinal);

// Each connected server tells BrainAgent how to use its own tools directly (McpServerOptions.
// ServerInstructions on the server side - see each Mcp* project's own Program.cs) rather than that
// domain knowledge being hand-authored centrally in Agents/BrainAgent.yaml. Appended once at
// startup, after the YAML's own cross-cutting instructions (multi-part requests, reporting
// discipline, resolving history) that don't belong to any one domain.
var domainInstructions = mcpClients
    .Select(c => c.ServerInstructions)
    .Where(instructions => !string.IsNullOrWhiteSpace(instructions));
agentConfig.Instructions = string.Join("\n\n", [agentConfig.Instructions, .. domainInstructions]);

// Resolves the AgentFactory singleton eagerly (forcing its DI construction now, not on first chat
// request) so its tool-retrieval index can be built before the app starts serving - real semantic
// embeddings (Qwen/Qwen3-Embedding-8B-class model via a second local vLLM instance, not Ollama, not
// a hash-based fake - see ToolRetrievalIndex's own doc comment) are load-bearing for tool-call
// correctness now, so an unreachable embedding endpoint must fail the app at boot, not silently at
// the first real operator turn. Built here, after Build() but before Run(), because
// AgentFactory.BuildTemplateTools() needs the real per-tool wrapping only a fully-constructed
// AgentFactory can produce - see AgentFactory.RetrievalIndex's own doc comment for why this can't
// be a constructor dependency.
try
{
    var embeddingClient = new EmbeddingClient(embeddingOptions.Model, new ApiKeyCredential("not-needed"),
        new OpenAIClientOptions { Endpoint = new Uri(embeddingOptions.Endpoint) });
    var embeddingGenerator = embeddingClient.AsIEmbeddingGenerator();
    var templateTools = agentFactory.BuildTemplateTools();
    agentFactory.RetrievalIndex = await ToolRetrievalIndex.BuildAsync(templateTools, toolNameToServerName, embeddingGenerator, CancellationToken.None);
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Failed to build the tool retrieval index using the configured embedding endpoint. " +
        $"Please ensure your embedding server is running and accessible. {ex.Message}", ex);
}

// Kestrel is already listening (app.StartAsync() above) - just block until shutdown, the
// equivalent second half of what app.Run() would otherwise have done as one call.
await app.WaitForShutdownAsync();

public partial class Program;

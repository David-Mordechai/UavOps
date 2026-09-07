using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Serilog;
using System.ClientModel;
using UavOps.Agent.Agents;
using UavOps.Agent.Agents.MaintenanceAgent;
using UavOps.Agent.Agents.MoavAgent.Hubs;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Agents.MoavAgent.Operations.Remote;
using UavOps.Agent.Agents.MoavAgent.Simulation;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Simulator.Fake;
using UavOps.Agent.Tooling;
using UavOps.Agent.Watchdog.Fake;

var builder = WebApplication.CreateBuilder(args);

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

var operationBackend = Enum.TryParse<OperationBackend>(builder.Configuration["OperationBackend"], ignoreCase: true, out var backend)
    ? backend
    : OperationBackend.Simulated;

var agentConfig = AgentConfigLoader.Load(Path.Combine(builder.Environment.ContentRootPath, "AgentsConfig", "BrainAgent.yaml"));

// Single place to see/change which backend+model BrainAgent uses, instead of that being buried
// in AgentsConfig/BrainAgent.yaml (which stays focused on behavior/content). Applied directly onto
// the already-loaded AgentConfig, before validation runs so it sees the final provider/model it
// will actually use.
var agentModelOptions = builder.Configuration.GetSection("AgentModels").Get<AgentModelOptions>();
if (agentModelOptions is not null)
{
    agentConfig.Provider = agentModelOptions.Provider;
    agentConfig.Model = agentModelOptions.Model;
}

// Reflects over IOperationService's/ISimulatorService's methods — no network call, no
// remotely-fetched spec — and validates BrainAgent's Tools[] against them before the app is
// allowed to start.
var catalog = new OperationCatalog(typeof(IOperationService));
var simulatorCatalog = new OperationCatalog(typeof(ISimulatorService));
var watchdogCatalog = new OperationCatalog(typeof(IWatchdogService));
var watchdogConfigCatalog = new OperationCatalog(typeof(IWatchdogConfigService));
AgentConfigValidator.Validate(agentConfig, catalog, simulatorCatalog, watchdogCatalog, watchdogConfigCatalog, openAiOptions);

var simulatorOptions = builder.Configuration.GetSection(SimulatorOptions.SectionName).Get<SimulatorOptions>()
    ?? new SimulatorOptions();

var simulatorBackend = Enum.TryParse<SimulatorBackend>(builder.Configuration["SimulatorBackend"], ignoreCase: true, out var simBackend)
    ? simBackend
    : SimulatorBackend.Fake;

var watchdogOptions = builder.Configuration.GetSection(WatchdogOptions.SectionName).Get<WatchdogOptions>()
    ?? new WatchdogOptions();

var watchdogBackend = Enum.TryParse<WatchdogBackend>(builder.Configuration["WatchdogBackend"], ignoreCase: true, out var wdBackend)
    ? wdBackend
    : WatchdogBackend.Fake;

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
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(simulatorCatalog);
builder.Services.AddSingleton(simulatorOptions);
builder.Services.AddSingleton(watchdogCatalog);
builder.Services.AddSingleton(watchdogOptions);
builder.Services.AddSingleton(watchdogConfigCatalog);

// A real queue (not just a "busy" flag) so a second lesson request while one is already running
// waits its turn instead of being rejected — consumed by SimulatorLessonJobProcessor below.
builder.Services.AddSingleton<ISimulatorLessonJobQueue, SimulatorLessonJobQueue>();

if (simulatorBackend == SimulatorBackend.Real)
{
    builder.Services.AddSingleton<IVmwareController, VmwareController>();
    builder.Services.AddSingleton<ILocalLessonRunner, LocalLessonRunner>();
    builder.Services.AddSingleton<ILessonExecutor, LocalLessonExecutor>();
    builder.Services.AddSingleton<ISimulatorService, SimulatorService>();
}
else
{
    // No VMware/VM required — the default, so BrainAgent's simulator tools (all five, the
    // operator lesson-choice prompt, the run-lesson confirmation) can be exercised end to end on
    // any machine with nothing installed. Registered via the Fake DLL's own IoC extension
    // (UavOps.Agent.Simulator.Fake) rather than this project registering the fake types itself.
    builder.Services.AddFakeSimulator();
}

if (watchdogBackend == WatchdogBackend.Real)
{
    builder.Services.AddHttpClient("Watchdog", c => c.Timeout = TimeSpan.FromSeconds(watchdogOptions.HttpTimeoutSeconds));
#pragma warning disable CA1416 // WindowsServiceController is [SupportedOSPlatform("windows")] — only reached when WatchdogBackend=Real, and this app already assumes Windows (VMware/PowerShell).
    builder.Services.AddSingleton<IWindowsServiceController, WindowsServiceController>();
#pragma warning restore CA1416
    builder.Services.AddSingleton<IWatchdogHealthStore, WatchdogHealthStore>();
    builder.Services.AddSingleton<IWatchdogService, WatchdogService>();
    // Only meaningful under Real — there's no watchdog HTTP endpoint to poll under Fake, so this
    // hosted service (unlike SimulatorLessonJobProcessor) is registered conditionally.
    builder.Services.AddHostedService<WatchdogHealthPoller>();

    builder.Services.AddSingleton<IServiceConfigFileStore, ServiceConfigFileStore>();
    builder.Services.AddSingleton<IExecutablePathResolver, ExecutablePathResolver>();
    builder.Services.AddSingleton<IServiceExecutableLocator, ServiceExecutableLocator>();
    builder.Services.AddSingleton<IWatchdogConfigService, WatchdogConfigService>();
}
else
{
    // No watchdog HTTP endpoint or real Windows services required — the default, so BrainAgent's
    // watchdog tools can be exercised end to end on any machine with nothing installed. Registered
    // via the Fake DLL's own IoC extension (UavOps.Agent.Watchdog.Fake), same pattern as
    // AddFakeSimulator above.
    builder.Services.AddFakeWatchdog();
}

// A confirmation reply (or an operation's SubmitCommandResult reply) is just another call on
// the same connection while the original call is still in flight, so raise the per-connection
// parallel-invocation limit above SignalR's default of 1 (which would otherwise queue the reply
// behind the in-progress call until it times out). This one call configures both hubs mapped below.
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 10);

// Always registered — a fleet command client can connect at any time without an app restart,
// even while currently running OperationBackend=Simulated.
builder.Services.AddSingleton<IRemoteOperationBroker, RemoteOperationBroker>();

if (operationBackend == OperationBackend.SignalR)
{
    builder.Services.AddSingleton<IOperationService, RemoteOperationService>();
}
else
{
    builder.Services.AddSingleton<IOperationService, SimulatedUavOperationService>();
}

builder.Services.AddSingleton<ToolInvocationLogger>();
builder.Services.AddSingleton<ConfirmationGate>();
builder.Services.AddSingleton<OperatorPromptGate>();

builder.Services.AddSingleton<Func<string, string?, IChatClient>>(sp => (modelName, provider) =>
{
    IChatClient inner;
    if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        // AgentConfigValidator already guarantees ApiKey is set for any agent that reaches this
        // branch — the null-forgiving operator below reflects that, not an unchecked assumption.
        var options = sp.GetRequiredService<OpenAiOptions>();
        var chatClient = new ChatClient(modelName, new ApiKeyCredential(options.ApiKey!),
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
        catalog,
        sp.GetRequiredService<IOperationService>(),
        simulatorCatalog,
        sp.GetRequiredService<ISimulatorService>(),
        watchdogCatalog,
        sp.GetRequiredService<IWatchdogService>(),
        watchdogConfigCatalog,
        sp.GetRequiredService<IWatchdogConfigService>(),
        sp.GetRequiredService<RetrievalOptions>(),
        sp.GetRequiredService<MemoryOptions>(),
        sp.GetRequiredService<ToolInvocationLogger>(),
        sp.GetRequiredService<ConfirmationGate>(),
        sp.GetRequiredService<OperatorPromptGate>()
    ));

builder.Services.AddSingleton<MainAgentOrchestrator>();

// Backend-agnostic (only depends on ILessonExecutor, registered above per SimulatorBackend) —
// one consumer processes ISimulatorLessonJobQueue jobs one at a time under its own lifetime token
// (app shutdown only, not any individual chat request's), so a browser disconnecting mid-lesson
// can't affect a run already handed off to the queue.
builder.Services.AddHostedService<SimulatorLessonJobProcessor>();

var app = builder.Build();

// Resolves the AgentFactory singleton eagerly (forcing its DI construction now, not on first chat
// request) so its tool-retrieval index can be built before the app starts serving - real semantic
// embeddings (Qwen/Qwen3-Embedding-8B-class model via a second local vLLM instance, not Ollama, not
// a hash-based fake - see ToolRetrievalIndex's own doc comment) are load-bearing for tool-call
// correctness now, so an unreachable embedding endpoint must fail the app at boot, not silently at
// the first real operator turn. Built here, after Build() but before Run(), because
// AgentFactory.BuildTemplateTools() needs the real per-tool wrapping only a fully-constructed
// AgentFactory can produce - see AgentFactory.RetrievalIndex's own doc comment for why this can't
// be a constructor dependency.
var agentFactory = app.Services.GetRequiredService<AgentFactory>();
try
{
    var embeddingClient = new EmbeddingClient(embeddingOptions.Model, new ApiKeyCredential("not-needed"),
        new OpenAIClientOptions { Endpoint = new Uri(embeddingOptions.Endpoint) });
    var embeddingGenerator = embeddingClient.AsIEmbeddingGenerator();
    var templateTools = agentFactory.BuildTemplateTools();
    agentFactory.RetrievalIndex = await ToolRetrievalIndex.BuildAsync(templateTools, embeddingGenerator, CancellationToken.None);
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Failed to build the tool retrieval index using the configured embedding endpoint. " +
        $"Please ensure your embedding server is running and accessible. {ex.Message}", ex);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ChatHub>("/chatHub");
app.MapHub<OperationHub>("/uavCommandHub");

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    ollamaModel = ollamaOptions.DefaultModel,
    operationBackend = operationBackend.ToString(),
    simulatorBackend = simulatorBackend.ToString(),
    watchdogBackend = watchdogBackend.ToString(),
    operations = catalog.Operations.Count,
    simulatorOperations = simulatorCatalog.Operations.Count,
    watchdogOperations = watchdogCatalog.Operations.Count,
    watchdogConfigOperations = watchdogConfigCatalog.Operations.Count,
    retrievalTools = agentFactory.RetrievalIndex.Count
}));

app.MapGet("/api/agent-graph", () => Results.Ok(AgentGraphProjector.Build(agentConfig)));

app.Run();

public partial class Program;

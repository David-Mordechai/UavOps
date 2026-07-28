using Microsoft.Extensions.AI;
using OllamaSharp;
using Serilog;
using UavOps.Agent.Agents;
using UavOps.Agent.Hubs;
using UavOps.Agent.Operations;
using UavOps.Agent.Operations.Remote;
using UavOps.Agent.Options;
using UavOps.Agent.Simulation;
using UavOps.Agent.Tooling;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).WriteTo.Console());

var ollamaOptions = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>()
    ?? throw new InvalidOperationException($"Missing '{OllamaOptions.SectionName}' configuration section.");

var remoteOperationOptions = builder.Configuration.GetSection(RemoteOperationOptions.SectionName).Get<RemoteOperationOptions>()
    ?? new RemoteOperationOptions();

var operationBackend = Enum.TryParse<OperationBackend>(builder.Configuration["OperationBackend"], ignoreCase: true, out var backend)
    ? backend
    : OperationBackend.Simulated;

var agentsConfig = AgentConfigLoader.LoadFromDirectory(Path.Combine(builder.Environment.ContentRootPath, "AgentsConfig"));

// Reflects over IOperationService's methods — no network call, no remotely-fetched spec — and
// validates every agent's Tools[] against it before the app is allowed to start.
var catalog = new OperationCatalog(typeof(IOperationService));
AgentConfigValidator.Validate(agentsConfig, catalog);

IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
if (string.Equals(ollamaOptions.EmbeddingModel, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    embeddingGenerator = new InMemoryEmbeddingGenerator();
}
else
{
    embeddingGenerator = new OllamaApiClient(new Uri(ollamaOptions.Endpoint), ollamaOptions.EmbeddingModel);
}
builder.Services.AddSingleton(embeddingGenerator);

AgentRetrievalIndex retrievalIndex;
try
{
    retrievalIndex = await AgentRetrievalIndex.BuildAsync(agentsConfig, embeddingGenerator, CancellationToken.None);
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Failed to build the agent retrieval index using the configured embedding generator. " +
        $"Please ensure your embedding provider is running and accessible. {ex.Message}", ex);
}

var retrievalOptions = builder.Configuration.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>()
    ?? new RetrievalOptions();

builder.Services.AddSingleton(ollamaOptions);
builder.Services.AddSingleton(retrievalOptions);
builder.Services.AddSingleton(retrievalIndex);
builder.Services.AddSingleton(remoteOperationOptions);
builder.Services.AddSingleton(agentsConfig);
builder.Services.AddSingleton(catalog);

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

builder.Services.AddSingleton<Func<string, IChatClient>>(sp => modelName =>
{
    var options = sp.GetRequiredService<OllamaOptions>();
    var ollama = new OllamaApiClient(new Uri(options.Endpoint), modelName);
    return new FunctionInvokingChatClient(ollama) { AllowConcurrentInvocation = true };
});

builder.Services.AddSingleton<AgentFactory>(sp =>
    new AgentFactory(
        sp.GetRequiredService<Func<string, IChatClient>>(),
        ollamaOptions.DefaultModel,
        sp.GetRequiredService<Dictionary<string, AgentConfig>>(),
        sp.GetRequiredService<OperationCatalog>(),
        sp.GetRequiredService<IOperationService>(),
        sp.GetRequiredService<AgentRetrievalIndex>(),
        sp.GetRequiredService<RetrievalOptions>(),
        sp.GetRequiredService<ToolInvocationLogger>(),
        sp.GetRequiredService<ConfirmationGate>(),
        sp.GetRequiredService<ILogger<AgentFactory>>()
    ));

builder.Services.AddSingleton<MainAgentOrchestrator>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ChatHub>("/chatHub");
app.MapHub<OperationHub>("/uavCommandHub");

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    ollamaModel = ollamaOptions.DefaultModel,
    operationBackend = operationBackend.ToString(),
    operations = catalog.Operations.Count,
    retrievalAgents = retrievalIndex.Count
}));

app.Run();

public partial class Program;

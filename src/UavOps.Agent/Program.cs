using Microsoft.OpenApi.Readers;
using Serilog;
using UavOps.Agent.Agents;
using UavOps.Agent.Hubs;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).WriteTo.Console());

var ollamaOptions = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>()
    ?? throw new InvalidOperationException($"Missing '{OllamaOptions.SectionName}' configuration section.");

var uavApiOptions = builder.Configuration.GetSection(UavApiOptions.SectionName).Get<UavApiOptions>()
    ?? throw new InvalidOperationException($"Missing '{UavApiOptions.SectionName}' configuration section.");

var agentsConfig = builder.Configuration.GetSection("Agents").Get<Dictionary<string, AgentConfig>>()
    ?? throw new InvalidOperationException("Missing 'Agents' configuration section.");

// Fetch + parse the UAV app's OpenAPI spec at boot (fail fast, not on first chat message) and
// validate every agent's Tools[] against it before the app is allowed to start.
OpenApiToolCatalog toolCatalog;
using (var bootstrapClient = new HttpClient())
{
    if (!string.IsNullOrWhiteSpace(uavApiOptions.AuthHeader))
    {
        bootstrapClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", uavApiOptions.AuthHeader);
    }

    Stream specStream;
    try
    {
        specStream = await bootstrapClient.GetStreamAsync(uavApiOptions.OpenApiUrl);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Could not reach the UAV API's OpenAPI spec at '{uavApiOptions.OpenApiUrl}'. " +
            "Is the UAV API running? Check UavApi:OpenApiUrl in appsettings.json.", ex);
    }

    var document = new OpenApiStreamReader().Read(specStream, out var diagnostic);
    if (diagnostic.Errors.Count > 0)
    {
        throw new InvalidOperationException(
            $"Failed to parse the OpenAPI spec from '{uavApiOptions.OpenApiUrl}': {string.Join("; ", diagnostic.Errors)}");
    }

    toolCatalog = new OpenApiToolCatalog(document);
}

AgentConfigValidator.Validate(agentsConfig, toolCatalog);

builder.Services.AddSingleton(ollamaOptions);
builder.Services.AddSingleton(uavApiOptions);
builder.Services.AddSingleton(agentsConfig);
builder.Services.AddSingleton(toolCatalog);

// Confirmation approvals must be able to reach the hub while a chat turn's SendMessage call is
// still in flight on the same connection, so raise the per-connection parallel-invocation limit
// above SignalR's default of 1 (which would otherwise queue SendConfirmationResponse behind the
// still-running SendMessage call until it times out).
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 10);

builder.Services.AddHttpClient<UavApiToolInvoker>((sp, client) =>
{
    var opts = sp.GetRequiredService<UavApiOptions>();
    client.BaseAddress = new Uri(opts.BaseUrl);
    if (!string.IsNullOrWhiteSpace(opts.AuthHeader))
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", opts.AuthHeader);
    }
});

builder.Services.AddSingleton<ToolInvocationLogger>();
builder.Services.AddSingleton<ConfirmationGate>();
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<MainAgentOrchestrator>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ChatHub>("/chatHub");

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    ollamaModel = ollamaOptions.DefaultModel,
    uavApi = uavApiOptions.BaseUrl,
    operationCount = toolCatalog.Operations.Count
}));

app.Run();

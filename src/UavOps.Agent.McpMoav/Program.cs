using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpMoav;

// ContentRootPath must be this project's own output directory, not left to default to the
// process's working directory - UavOps.Agent spawns this as a child process with ITS OWN content
// root as the working directory (so relative dotnet-exec paths in BrainAgent.yaml resolve
// consistently - see UavOps.Agent's Program.cs), which means Host.CreateApplicationBuilder would
// otherwise silently load UavOps.Agent's own appsettings.json instead of this project's - live-
// reproduced: OperationBackend: SignalR here had zero effect until this fix, because the value
// actually being read was the host's own appsettings.json (which no longer even has that key).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Stdout is the MCP transport channel itself - any log line written there would corrupt the
// protocol stream, so every log has to go to stderr instead (the SDK's own documented setup).
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var operationBackend = Enum.TryParse<OperationBackend>(builder.Configuration["OperationBackend"], ignoreCase: true, out var backend)
    ? backend
    : OperationBackend.Simulated;

if (operationBackend == OperationBackend.SignalR)
{
    // The real Moav-hardware path: relay every MoavTools call into UavOps.Agent's own hub (see
    // ChatHub's own doc comment) instead of answering in-memory. Started here, before the MCP
    // server begins accepting tool calls, and fails the whole process if it can't connect - same
    // fail-fast-at-boot posture this codebase already applies to its other external dependencies
    // (the embedding endpoint, each MCP server the host itself connects to), rather than silently
    // leaving MoavTools unable to reach the real Moav client on first use.
    var hostHubUrl = builder.Configuration["HostHubUrl"]
        ?? throw new InvalidOperationException("Missing 'HostHubUrl' configuration - required when OperationBackend is SignalR.");
    // "?client=relay" marks this connection as McpMoav's own relay client, not the real Moav
    // hardware - ChatHub's connection tracking checks for it so this connection (and any
    // reconnect WithAutomaticReconnect triggers, e.g. after a transient network blip) never gets
    // mistaken for a real Moav-command target and silently steals the broker's tracked connection
    // away from the actual hardware client.
    var hubConnection = new HubConnectionBuilder().WithUrl($"{hostHubUrl}?client=relay").WithAutomaticReconnect().Build();
    await hubConnection.StartAsync();
    builder.Services.AddSingleton(hubConnection);
    builder.Services.AddSingleton<IOperationService, MoavRelayService>();
}
else
{
    builder.Services.AddSingleton<IOperationService, SimulatedUavOperationService>();
}

// Every AI-facing string for this domain - ServerInstructions, tool descriptions, parameter
// descriptions, and the readOnly/destructive annotations - lives in this project's own
// ToolsConfig.yaml, not in C# attributes, so changing wording needs only a YAML edit and a process
// restart, never a rebuild (matches how UavOps.Agent's own Agents/BrainAgent.yaml already works -
// see McpToolsConfig/McpToolsBuilder's own doc comments for why this moved off attributes and how
// it was verified against the actual installed MCP SDK before being built this way).
var toolsConfig = McpToolsConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "ToolsConfig.yaml"));
var tools = McpToolsBuilder.Build(typeof(MoavTools), toolsConfig, builder.Services);

builder.Services
    .AddMcpServer(options => options.ServerInstructions = toolsConfig.ServerInstructions)
    .WithStdioServerTransport()
    .WithTools(tools);

await builder.Build().RunAsync();

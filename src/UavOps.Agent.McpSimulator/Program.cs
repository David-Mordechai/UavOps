using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpSimulator;
using UavOps.Agent.Simulator.Fake;

// ContentRootPath must be this project's own output directory, not left to default to the
// process's working directory - UavOps.Agent spawns this as a child process with ITS OWN content
// root as the working directory (so relative dotnet-exec paths in BrainAgent.yaml resolve
// consistently - see UavOps.Agent's Program.cs), which means Host.CreateApplicationBuilder would
// otherwise silently load UavOps.Agent's own appsettings.json instead of this project's - live-
// reproduced for the sibling McpMoav project (OperationBackend: SignalR there had zero effect
// until this same fix); SimulatorBackend/WatchdogBackend happened to mask this same bug here since
// both projects' intended defaults coincided with what the host's own file (or its absence) fell
// back to.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Stdout is the MCP transport channel itself - any log line written there would corrupt the
// protocol stream, so every log has to go to stderr instead (the SDK's own documented setup).
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var simulatorOptions = builder.Configuration.GetSection(SimulatorOptions.SectionName).Get<SimulatorOptions>()
    ?? new SimulatorOptions();
builder.Services.AddSingleton(simulatorOptions);

var simulatorBackend = Enum.TryParse<SimulatorBackend>(builder.Configuration["SimulatorBackend"], ignoreCase: true, out var backend)
    ? backend
    : SimulatorBackend.Fake;

if (simulatorBackend == SimulatorBackend.Real)
{
    builder.Services.AddSingleton<IVmwareController, VmwareController>();
    builder.Services.AddSingleton<ILessonLister, LocalLessonLister>();
    builder.Services.AddSingleton<ISimulatorInfraService, SimulatorInfraService>();
    builder.Services.AddSingleton<ILocalLessonRunner, LocalLessonRunner>();
    builder.Services.AddSingleton<ILessonExecutor, LocalLessonExecutor>();
}
else
{
    // No VMware/VM required - the default, so this server's tools can be exercised end to end on
    // any machine with nothing installed. Registered via the Fake DLL's own IoC extension
    // (UavOps.Agent.Simulator.Fake) rather than this project registering the fake types itself.
    builder.Services.AddFakeSimulatorInfra();
    builder.Services.AddSingleton<ILessonExecutor, FakeLessonExecutor>();
}

// The background lesson pipeline - unconditional, regardless of SimulatorBackend, since running a
// chosen lesson to completion is orthogonal to whether VM control is Real or Fake.
var hostChatHubUrl = builder.Configuration["HostChatHubUrl"]
    ?? throw new InvalidOperationException("Missing 'HostChatHubUrl' configuration.");
var hubConnection = new HubConnectionBuilder().WithUrl(hostChatHubUrl).WithAutomaticReconnect().Build();
builder.Services.AddSingleton(hubConnection);
builder.Services.AddSingleton<ILessonOutcomeNotifier, HubLessonOutcomeNotifier>();
builder.Services.AddSingleton<ISimulatorLessonJobQueue, SimulatorLessonJobQueue>();
builder.Services.AddHostedService<HubConnectionStarter>();
builder.Services.AddHostedService<SimulatorLessonJobProcessor>();

// Every AI-facing string for this domain - ServerInstructions, tool descriptions, parameter
// descriptions, and the readOnly/destructive annotations - lives in this project's own
// ToolsConfig.yaml, not in C# attributes, so changing wording needs only a YAML edit and a process
// restart, never a rebuild (matches how UavOps.Agent's own Agents/BrainAgent.yaml already works -
// see McpToolsConfig/McpToolsBuilder's own doc comments for why this moved off attributes and how
// it was verified against the actual installed MCP SDK before being built this way).
var toolsConfig = McpToolsConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "ToolsConfig.yaml"));
var tools = McpToolsBuilder.Build(typeof(SimulatorTools), toolsConfig, builder.Services);

builder.Services
    .AddMcpServer(options => options.ServerInstructions = toolsConfig.ServerInstructions)
    .WithStdioServerTransport()
    .WithTools(tools);

await builder.Build().RunAsync();

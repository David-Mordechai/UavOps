using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;
using UavOps.Agent.McpWatchdog;
using UavOps.Agent.Watchdog.Fake;

// ContentRootPath must be this project's own output directory, not left to default to the
// process's working directory - UavOps.Agent spawns this as a child process with ITS OWN content
// root as the working directory (so relative dotnet-exec paths in BrainAgent.yaml resolve
// consistently - see UavOps.Agent's Program.cs), which means Host.CreateApplicationBuilder would
// otherwise silently load UavOps.Agent's own appsettings.json instead of this project's - live-
// reproduced for the sibling McpMoav project (OperationBackend: SignalR there had zero effect
// until this same fix); WatchdogBackend/SimulatorBackend happened to mask this same bug here since
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

var watchdogOptions = builder.Configuration.GetSection(WatchdogOptions.SectionName).Get<WatchdogOptions>()
    ?? new WatchdogOptions();
builder.Services.AddSingleton(watchdogOptions);

var watchdogBackend = Enum.TryParse<WatchdogBackend>(builder.Configuration["WatchdogBackend"], ignoreCase: true, out var backend)
    ? backend
    : WatchdogBackend.Fake;

if (watchdogBackend == WatchdogBackend.Real)
{
    builder.Services.AddHttpClient("Watchdog", c => c.Timeout = TimeSpan.FromSeconds(watchdogOptions.HttpTimeoutSeconds));
#pragma warning disable CA1416 // WindowsServiceController is [SupportedOSPlatform("windows")] - only reached under WatchdogBackend=Real, and this whole domain already assumes Windows.
    builder.Services.AddSingleton<IWindowsServiceController, WindowsServiceController>();
#pragma warning restore CA1416
    builder.Services.AddSingleton<IWatchdogHealthStore, WatchdogHealthStore>();
    builder.Services.AddSingleton<IWatchdogService, WatchdogService>();
    builder.Services.AddHostedService<WatchdogHealthPoller>();

    builder.Services.AddSingleton<IServiceConfigFileStore, ServiceConfigFileStore>();
    builder.Services.AddSingleton<IExecutablePathResolver, ExecutablePathResolver>();
    builder.Services.AddSingleton<IServiceExecutableLocator, ServiceExecutableLocator>();
    builder.Services.AddSingleton<IWatchdogConfigService, WatchdogConfigService>();
}
else
{
    // No watchdog HTTP endpoint or real Windows services required - the default, so this server's
    // tools can be exercised end to end on any machine with nothing installed.
    builder.Services.AddFakeWatchdog();
}

// Every AI-facing string for this domain - ServerInstructions, tool descriptions, parameter
// descriptions, and the readOnly/destructive annotations - lives in this project's own
// ToolsConfig.yaml, not in C# attributes, so changing wording needs only a YAML edit and a process
// restart, never a rebuild (matches how UavOps.Agent's own Agents/BrainAgent.yaml already works -
// see McpToolsConfig/McpToolsBuilder's own doc comments for why this moved off attributes and how
// it was verified against the actual installed MCP SDK before being built this way).
var toolsConfig = McpToolsConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "ToolsConfig.yaml"));
var tools = McpToolsBuilder.Build(typeof(WatchdogTools), toolsConfig, builder.Services);

builder.Services
    .AddMcpServer(options => options.ServerInstructions = toolsConfig.ServerInstructions)
    .WithStdioServerTransport()
    .WithTools(tools);

await builder.Build().RunAsync();

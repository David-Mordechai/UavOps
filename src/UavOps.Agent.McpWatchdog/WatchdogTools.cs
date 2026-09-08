using System.Text.Json;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpWatchdog;

/// <summary>
/// The watchdog domain's MCP tools - health/process control (<see cref="IWatchdogService"/>) and
/// declarative service configuration (<see cref="IWatchdogConfigService"/>) - see
/// <see cref="UavOps.Agent.McpMoav.MoavTools"/>'s own doc comment for why this project depends on
/// interfaces (<see cref="IWatchdogService"/>/<see cref="IWatchdogConfigService"/>), not a concrete
/// implementation: <see cref="Program"/> decides Real vs. Fake via DI, transparently to every tool
/// method here. No <c>[McpServerTool]</c>/<c>[Description]</c> attributes here anymore - every
/// method's name/description/parameter descriptions/annotations come from this project's own
/// <c>ToolsConfig.yaml</c>, built at startup by <see cref="McpToolsBuilder"/> (<c>Program.cs</c>) -
/// editing a description or adding a parameter description needs only a YAML edit and a process
/// restart, never a rebuild. Method names still have to match that YAML's <c>operation:</c> entries
/// exactly (case-sensitive) - <see cref="McpToolsBuilder.Build"/> fails fast at startup if either
/// side has an entry the other doesn't.
///
/// <c>StartService</c>/<c>StopService</c>/<c>RestartService</c> take no model-visible parameter at
/// all - the watchdog's own real Windows service name ("Moav.Watchdog.Service") is hardcoded here,
/// the same "fixedParameters, never shown to the model" behavior the old YAML config declared, just
/// expressed directly in code now that there's no separate config layer between the model's tool
/// call and this method.
/// </summary>
public static class WatchdogTools
{
    private const string WatchdogServiceName = "Moav.Watchdog.Service";

    private static readonly JsonSerializerOptions ResultSerializeOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string ToResultText(OperationResult result) =>
        result.Success ? JsonSerializer.Serialize(result.Value, ResultSerializeOptions) : $"Error: {result.ErrorMessage}";

    public static async Task<string> GetServicesHealth(IWatchdogService watchdog, CancellationToken cancellationToken) =>
        ToResultText(await watchdog.GetServicesHealth(cancellationToken));

    public static async Task<string> StartService(IWatchdogService watchdog, CancellationToken cancellationToken) =>
        ToResultText(await watchdog.StartService(WatchdogServiceName, cancellationToken));

    public static async Task<string> StopService(IWatchdogService watchdog, CancellationToken cancellationToken) =>
        ToResultText(await watchdog.StopService(WatchdogServiceName, cancellationToken));

    public static async Task<string> RestartService(IWatchdogService watchdog, CancellationToken cancellationToken) =>
        ToResultText(await watchdog.RestartService(WatchdogServiceName, cancellationToken));

    public static async Task<string> ListConfigurations(IWatchdogConfigService watchdogConfig, CancellationToken cancellationToken) =>
        ToResultText(await watchdogConfig.ListConfigurations(cancellationToken));

    public static async Task<string> ListConfiguredServices(IWatchdogConfigService watchdogConfig, string configurationName, CancellationToken cancellationToken) =>
        ToResultText(await watchdogConfig.ListConfiguredServices(configurationName, cancellationToken));

    public static async Task<string> AddConfiguredService(
        IWatchdogConfigService watchdogConfig,
        string configurationName,
        string description,
        string? executable,
        List<string>? args,
        string? id,
        bool? disabled,
        int? retries,
        bool? isManaged,
        string? healthEndPoint,
        string? group,
        CancellationToken cancellationToken) =>
        ToResultText(await watchdogConfig.AddConfiguredService(
            configurationName, description, executable, args, id, disabled, retries, isManaged, healthEndPoint, group, cancellationToken));

    public static async Task<string> UpdateConfiguredService(
        IWatchdogConfigService watchdogConfig,
        string configurationName,
        string description,
        string? newDescription,
        string? executable,
        List<string>? args,
        string? id,
        bool? disabled,
        int? retries,
        bool? isManaged,
        string? healthEndPoint,
        string? group,
        CancellationToken cancellationToken) =>
        ToResultText(await watchdogConfig.UpdateConfiguredService(
            configurationName, description, newDescription, executable, args, id, disabled, retries, isManaged, healthEndPoint, group, cancellationToken));

    public static async Task<string> RemoveConfiguredService(IWatchdogConfigService watchdogConfig, string configurationName, string description, CancellationToken cancellationToken) =>
        ToResultText(await watchdogConfig.RemoveConfiguredService(configurationName, description, cancellationToken));
}

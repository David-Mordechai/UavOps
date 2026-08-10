using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace UavOps.Agent.Contracts;

/// <summary>
/// One service entry in a watchdog service-definition config file. Mirrors the real watchdog's
/// YAML schema (<c>description</c>, <c>executable</c>, <c>args</c>, <c>id</c>, <c>enabled</c>,
/// <c>disabled</c>, <c>retries</c>, <c>isManaged</c>, <c>healthEndPoint</c>, <c>group</c>) — used
/// both as the on-disk file-format model
/// (<c>Agents.MaintenanceAgent.IServiceConfigFileStore</c>) and as the JSON payload returned in
/// <see cref="OperationResult.Value"/> for <c>IWatchdogConfigService.ListConfiguredServices</c>.
/// Every field except <see cref="Description"/>/<see cref="Executable"/> is nullable so "not
/// specified" (omit from the file / leave unchanged on update) is distinguishable from an explicit
/// default value.
///
/// String-typed fields are annotated <c>[YamlMember(ScalarStyle = SingleQuoted)]</c> so
/// <c>ServiceConfigFileStore</c>'s writer quotes their *values* the same way the config file's own
/// documented examples do (e.g. <c>executable: '%MoavProducts%\...'</c>) — this is YamlDotNet's
/// own supported per-member styling hook, which (unlike a blanket <c>IYamlTypeConverter</c> for
/// <c>string</c>) only touches the value, never the mapping key, since keys and values otherwise
/// go through the same string-emission path.
/// </summary>
public sealed class ServiceConfigEntry
{
    /// <summary>Mandatory, must be unique within a configuration file — the identifier operators
    /// and Update/Remove use to refer to a service.</summary>
    [YamlMember(ScalarStyle = ScalarStyle.SingleQuoted)]
    public string Description { get; set; } = "";

    [YamlMember(ScalarStyle = ScalarStyle.SingleQuoted)]
    public string Executable { get; set; } = "";

    public List<string>? Args { get; set; }

    [YamlMember(ScalarStyle = ScalarStyle.SingleQuoted)]
    public string? Id { get; set; }

    public bool? Enabled { get; set; }
    public bool? Disabled { get; set; }
    public int? Retries { get; set; }
    public bool? IsManaged { get; set; }

    [YamlMember(ScalarStyle = ScalarStyle.SingleQuoted)]
    public string? HealthEndPoint { get; set; }

    [YamlMember(ScalarStyle = ScalarStyle.SingleQuoted)]
    public string? Group { get; set; }
}

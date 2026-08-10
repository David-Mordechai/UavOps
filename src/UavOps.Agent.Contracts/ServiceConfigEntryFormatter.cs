using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UavOps.Agent.Contracts;

/// <summary>
/// Renders a single <see cref="ServiceConfigEntry"/> as a YAML snippet, styled exactly like it
/// would appear in a real config file (a one-item block sequence, same quoting conventions as
/// <c>Agents.MaintenanceAgent.ServiceConfigFileStore</c> uses for the whole file) — so an operator
/// can visually verify what a tool call just wrote without opening the file themselves.
/// </summary>
public static class ServiceConfigEntryFormatter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ArgsYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static string Format(ServiceConfigEntry entry) => Serializer.Serialize(new List<ServiceConfigEntry> { entry });
}

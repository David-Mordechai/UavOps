using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UavOps.Agent.Options;

/// <summary>
/// Loads one <see cref="AgentConfig"/> per <c>*.yaml</c> file found anywhere under a directory —
/// replaces the old single <c>Agents</c> block in appsettings.json, which stopped scaling once the
/// agent roster grew. The agent's name is its filename (without extension), not a field inside
/// the file, so a filename/field mismatch can't happen — this also means nesting files into
/// per-agent subfolders (e.g. <c>AgentsConfig/MoavAgent/FlightControlAgent.yaml</c>) needs no
/// change here beyond searching recursively; an agent's position in the folder tree is purely
/// organizational; it plays no role in the agent graph itself (that's <c>children:</c>, in
/// <see cref="AgentConfig"/>).
/// </summary>
public static class AgentConfigLoader
{
    public static Dictionary<string, AgentConfig> LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new InvalidOperationException($"Agent config folder not found: '{directoryPath}'.");
        }

        var files = Directory.GetFiles(directoryPath, "*.yaml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No *.yaml files found in '{directoryPath}'.");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var agents = new Dictionary<string, AgentConfig>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                agents[name] = deserializer.Deserialize<AgentConfig>(File.ReadAllText(file))
                    ?? throw new InvalidOperationException("File is empty.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Failed to parse agent config '{file}': {ex.Message}", ex);
            }
        }

        return agents;
    }
}

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UavOps.Agent.Options;

/// <summary>
/// Loads the single <see cref="AgentConfig"/> from <c>Agents/BrainAgent.yaml</c> — one file,
/// one agent, since the multi-agent delegation tree this used to load recursively (per-agent
/// subfolders, filename-as-agent-name) was collapsed into one flat agent holding every real
/// operation directly.
/// </summary>
public static class AgentConfigLoader
{
    public static AgentConfig Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"Agent config file not found: '{filePath}'.");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        try
        {
            return deserializer.Deserialize<AgentConfig>(File.ReadAllText(filePath))
                ?? throw new InvalidOperationException("File is empty.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to parse agent config '{filePath}': {ex.Message}", ex);
        }
    }
}

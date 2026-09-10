using System.Text.Json;
using System.Text.Json.Nodes;

namespace UavOps.Agent.Options;

/// <summary>
/// Backs the Settings page's <c>/api/settings</c> endpoints. Reads/writes plain
/// <see cref="JsonObject"/>s, not strongly-typed DTOs — the shape mirrors each domain's own real
/// config sections directly (e.g. <c>agent.Ollama.Endpoint</c>, <c>mcpServers.moav.settings.
/// HostHubUrl</c>), so the frontend's generic form builder and this store never need a parallel
/// schema to stay in sync with <see cref="OllamaOptions"/>/<see cref="WatchdogOptions"/>/etc.
///
/// Persists to a new <c>appsettings.Local.json</c> per process (this host's own, plus one per MCP
/// child, located next to that server's own <c>appsettings.json</c> via its already-resolved dll
/// path in <see cref="AgentConfig.McpServers"/> — <c>Program.cs</c> resolves
/// <c>McpServerConfig.Args</c> to the real dll path once at startup, before this store or the MCP
/// connection loop ever runs, so both already agree on where a server's files live with no separate
/// resolution logic needed here) — never the checked-in <c>appsettings.json</c>, mirroring the
/// precedent already established for <c>McpServerPaths</c> deploy overrides. A saved value takes
/// effect on next restart for everything except <c>ExecutionMode</c> and each MCP server's
/// <c>enabled</c> flag (both read live elsewhere — see <see cref="LiveFields"/>).
///
/// <c>OpenAI:ApiKey</c> is never read into the GET payload and never written from a POST body —
/// this UI never persists secrets, matching the existing warning in <see cref="OpenAiOptions"/>.
/// </summary>
public sealed class SettingsStore(IConfiguration hostConfiguration, IHostEnvironment env, AgentConfig agentConfig)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Dotted paths (matching the shape GET returns) that apply without a restart — the
    /// frontend renders every other field with a "restart required" hint. Single source of truth;
    /// the frontend never hardcodes this itself.</summary>
    private static readonly string[] LiveFields =
    [
        "agent.ExecutionMode",
        "mcpServers.moav.enabled",
        "mcpServers.watchdog.enabled",
        "mcpServers.simulator.enabled"
    ];

    /// <summary>Dotted paths that are filesystem paths — the frontend validates these inline via
    /// <c>/api/validate-path</c>. Dictionary-valued path-like entries (e.g.
    /// <c>ExecutablePlaceholders</c>' values) are deliberately out of scope.</summary>
    private static readonly string[] PathFields =
    [
        "mcpServers.simulator.settings.Simulator.VmwareExecutablePath",
        "mcpServers.simulator.settings.Simulator.VmrunExecutablePath",
        "mcpServers.simulator.settings.Simulator.SimulatorVmxPath",
        "mcpServers.simulator.settings.Simulator.LessonsFolder",
        "mcpServers.watchdog.settings.Watchdog.ServiceConfigBasePath"
    ];

    public async Task<JsonObject> GetEffectiveSettingsAsync()
    {
        var agent = new JsonObject
        {
            ["Ollama"] = ReadHostSection("Ollama"),
            ["OpenAI"] = SanitizeOpenAi(ReadHostSection("OpenAI")),
            ["Embedding"] = ReadHostSection("Embedding"),
            ["AgentModels"] = ReadHostSection("AgentModels"),
            ["Retrieval"] = ReadHostSection("Retrieval"),
            ["Memory"] = ReadHostSection("Memory"),
            ["RemoteOperation"] = ReadHostSection("RemoteOperation"),
            ["ExecutionMode"] = hostConfiguration["ExecutionMode"] ?? "Confirm"
        };

        var enabledMap = hostConfiguration.GetSection(McpServerSelection.ConfigKey).Get<Dictionary<string, bool>>() ?? [];
        var mcpServers = new JsonObject();
        foreach (var serverConfig in agentConfig.McpServers)
        {
            var enabled = !enabledMap.TryGetValue(serverConfig.Name, out var e) || e;
            mcpServers[serverConfig.Name] = new JsonObject
            {
                ["enabled"] = enabled,
                ["settings"] = await ReadMcpDomainSettingsAsync(serverConfig)
            };
        }

        return new JsonObject
        {
            ["agent"] = agent,
            ["mcpServers"] = mcpServers,
            ["liveFields"] = new JsonArray([.. LiveFields.Select(f => (JsonNode)f)]),
            ["pathFields"] = new JsonArray([.. PathFields.Select(f => (JsonNode)f)])
        };
    }

    public async Task SaveAsync(JsonObject body)
    {
        await _writeLock.WaitAsync();
        try
        {
            var hostLocal = new JsonObject();

            if (body["agent"] is JsonObject agent)
            {
                foreach (var (key, value) in SanitizeAgentForWrite(agent))
                {
                    hostLocal[key] = value?.DeepClone();
                }
            }

            if (body["mcpServers"] is JsonObject mcpServersNode)
            {
                var enabledMap = new JsonObject();
                foreach (var serverConfig in agentConfig.McpServers)
                {
                    if (mcpServersNode[serverConfig.Name] is not JsonObject serverNode)
                    {
                        continue;
                    }

                    if (serverNode["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var enabled))
                    {
                        enabledMap[serverConfig.Name] = enabled;
                    }

                    if (serverNode["settings"] is JsonObject settingsNode)
                    {
                        await WriteMcpDomainLocalAsync(serverConfig, settingsNode);
                    }
                }

                if (enabledMap.Count > 0)
                {
                    hostLocal[McpServerSelection.ConfigKey] = enabledMap;
                }
            }

            await AtomicWriteAsync(Path.Combine(env.ContentRootPath, "appsettings.Local.json"), hostLocal);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private JsonNode ReadHostSection(string key) => ConfigSectionToJson(hostConfiguration.GetSection(key)) ?? new JsonObject();

    private static JsonNode? ConfigSectionToJson(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return ParseScalar(section.Value);
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = ConfigSectionToJson(child);
        }
        return obj;
    }

    private static JsonNode? ParseScalar(string? value)
    {
        if (value is null) return null;
        if (bool.TryParse(value, out var b)) return JsonValue.Create(b);
        if (int.TryParse(value, out var i)) return JsonValue.Create(i);
        if (double.TryParse(value, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    private static JsonNode SanitizeOpenAi(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var apiKeyKey = obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "ApiKey", StringComparison.OrdinalIgnoreCase));
            if (apiKeyKey is not null)
            {
                obj.Remove(apiKeyKey);
            }
        }
        return node;
    }

    private static JsonObject SanitizeAgentForWrite(JsonObject agent)
    {
        var clone = (JsonObject)agent.DeepClone();
        if (clone["OpenAI"] is JsonObject openAi)
        {
            var apiKeyKey = openAi.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "ApiKey", StringComparison.OrdinalIgnoreCase));
            if (apiKeyKey is not null)
            {
                openAi.Remove(apiKeyKey);
            }
        }
        return clone;
    }

    private static async Task<JsonNode> ReadMcpDomainSettingsAsync(McpServerConfig serverConfig)
    {
        var dir = ResolveServerDirectory(serverConfig);
        var baseNode = await ReadJsonFileAsync(Path.Combine(dir, "appsettings.json")) ?? new JsonObject();
        var localNode = await ReadJsonFileAsync(Path.Combine(dir, "appsettings.Local.json"));

        if (localNode is JsonObject localObj && baseNode is JsonObject baseObj)
        {
            foreach (var (key, value) in localObj)
            {
                baseObj[key] = value?.DeepClone();
            }
        }

        return baseNode;
    }

    private static async Task WriteMcpDomainLocalAsync(McpServerConfig serverConfig, JsonObject settings)
    {
        var dir = ResolveServerDirectory(serverConfig);
        await AtomicWriteAsync(Path.Combine(dir, "appsettings.Local.json"), settings);
    }

    /// <summary>By the time this store is used, <c>Program.cs</c> has already resolved every
    /// server's <see cref="McpServerConfig.Args"/> to <c>["exec", &lt;real dll path&gt;]</c> (deploy
    /// override or dev sibling-bin path — see <c>Program.cs</c>'s own comment) — the same singleton
    /// <see cref="AgentConfig"/> instance is injected here, so this store never re-derives that
    /// resolution, it just reads where the connection loop already decided each server lives.</summary>
    private static string ResolveServerDirectory(McpServerConfig serverConfig) =>
        Path.GetDirectoryName(serverConfig.Args[^1])
            ?? throw new InvalidOperationException($"Could not resolve a directory for MCP server '{serverConfig.Name}'.");

    private static async Task<JsonNode?> ReadJsonFileAsync(string path)
    {
        if (!File.Exists(path)) return null;
        var text = await File.ReadAllTextAsync(path);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task AtomicWriteAsync(string path, JsonNode content)
    {
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content.ToJsonString(WriteOptions));
        File.Move(tempPath, path, overwrite: true);
    }
}

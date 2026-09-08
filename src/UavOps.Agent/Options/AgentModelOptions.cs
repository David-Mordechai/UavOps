namespace UavOps.Agent.Options;

/// <summary>
/// The <c>AgentModels</c> appsettings.json section — which backend/model BrainAgent (the single
/// flat agent) uses. Deliberately kept out of YAML: <c>Agents/BrainAgent.yaml</c> describes
/// agent *behavior* (instructions, tools) — domain content, edited by whoever authors the persona —
/// while which model/provider serves it is a deployment/environment concern, the same category as
/// <see cref="OllamaOptions"/>/<see cref="OpenAiOptions"/> themselves. Applied onto the loaded
/// <see cref="AgentConfig"/> in <c>Program.cs</c>, after YAML loading and before
/// <see cref="AgentConfigValidator"/> runs.
/// </summary>
public sealed class AgentModelOptions
{
    /// <summary>"Ollama" or "OpenAI" (case-insensitive), or omitted/null for the local Ollama
    /// endpoint (today's default for every agent). See <see cref="AgentConfig.Provider"/>.</summary>
    public string? Provider { get; init; }

    public string? Model { get; init; }
}

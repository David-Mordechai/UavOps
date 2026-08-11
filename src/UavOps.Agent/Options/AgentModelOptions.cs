namespace UavOps.Agent.Options;

/// <summary>
/// One agent's entry in the <c>AgentModels</c> appsettings.json section — a single place to
/// see/change which backend and model every agent uses, instead of that being scattered across
/// eleven separate <c>AgentsConfig/*.yaml</c> files. Deliberately kept out of YAML: those files
/// describe agent *behavior* (instructions, tools, delegation) — which is domain content, edited
/// by whoever authors an agent's persona — while which model/provider serves it is a deployment/
/// environment concern, the same category as <see cref="OllamaOptions"/>/<see cref="OpenAiOptions"/>
/// themselves. Applied onto the matching <see cref="AgentConfig"/> in <c>Program.cs</c>, after YAML
/// loading and before <see cref="AgentConfigValidator"/> runs.
/// </summary>
public sealed class AgentModelOptions
{
    /// <summary>"Ollama" or "OpenAI" (case-insensitive), or omitted/null for the local Ollama
    /// endpoint (today's default for every agent). See <see cref="AgentConfig.Provider"/>.</summary>
    public string? Provider { get; init; }

    public string? Model { get; init; }
}

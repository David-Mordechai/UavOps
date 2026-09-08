namespace UavOps.Agent.Options;

/// <summary>
/// A generic OpenAI-compatible provider alongside the default Ollama one
/// (<see cref="OllamaOptions"/>) — selected per agent via <see cref="AgentModelOptions.Provider"/>
/// (see <see cref="AgentConfig.Provider"/>, populated from the <c>AgentModels</c> appsettings.json
/// section rather than YAML — see <c>Program.cs</c>). Deliberately not named after any one
/// backend: any server speaking the OpenAI Chat Completions wire format works here unchanged —
/// OpenRouter (the first one actually wired up), a self-hosted vLLM/llama.cpp server, Together,
/// Groq, real OpenAI, etc. — since it's all the same <c>Microsoft.Extensions.AI.OpenAI</c> client
/// underneath, just pointed at a different <see cref="Endpoint"/>/<see cref="ApiKey"/>. Only one
/// such backend is configured at a time; switching which one just means changing these two values.
///
/// Optional at startup, like <see cref="Contracts.SimulatorOptions"/> — most installs won't use
/// it. The only fail-fast check is in <see cref="AgentConfigValidator"/>, and
/// only when some agent actually opts into <c>Provider: OpenAI</c> without an
/// <see cref="ApiKey"/> configured.
/// </summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public string Endpoint { get; init; } = "https://openrouter.ai/api/v1";

    /// <summary>Never set this in appsettings.json — it would get committed. Set it via
    /// `dotnet user-secrets set "OpenAI:ApiKey" "..." --project src/UavOps.Agent` instead, which
    /// keeps it in a per-user file outside the repo entirely.</summary>
    public string? ApiKey { get; init; }
}

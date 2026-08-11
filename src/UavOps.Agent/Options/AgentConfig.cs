namespace UavOps.Agent.Options;

/// <summary>
/// One tool an agent may call. <see cref="Operation"/> must match a method name on
/// <c>IOperationService</c> (or <c>ISimulatorService</c>) exactly — that interface supplies the
/// mechanical parameter contract (names, CLR types). Everything the LLM actually reads —
/// <see cref="Description"/> and every parameter description — is authored here, never taken
/// from the interface.
///
/// Plain mutable properties, not <c>required</c>/<c>init</c> — this is deserialized by
/// YamlDotNet (see <c>AgentConfigLoader</c>), which builds objects via reflection and doesn't
/// participate in C#'s compile-time <c>required</c>-member checking, so a <c>required</c>
/// property here would silently end up default/empty instead of failing fast when a YAML file
/// omits it. <see cref="Options.AgentConfigValidator"/> checks non-emptiness explicitly instead.
/// </summary>
public sealed class AgentToolConfig
{
    public string Operation { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>An example chat utterance an operator could type that would plausibly trigger this
    /// tool — shown on hover in the agent-graph UI (see <see cref="AgentGraphProjector"/>). Always
    /// required (checked by <see cref="Options.AgentConfigValidator"/>), unlike
    /// <see cref="AgentConfig.ExampleUtterance"/> which is root-exempt.</summary>
    public string ExampleUtterance { get; set; } = "";

    /// <summary>"Operation" (default) resolves <see cref="Operation"/> against a catalog via
    /// reflection, as above. "OperatorPrompt" builds a bespoke ask-the-operator-and-wait tool
    /// instead (see <c>Agents.AskOperatorChoiceTool</c>) — <see cref="Operation"/> is then just
    /// the tool name shown to the LLM, not resolved against any catalog.</summary>
    public string Kind { get; set; } = "Operation";

    /// <summary>Parameter name -> description shown to the LLM. Must cover every parameter
    /// the operation requires that isn't listed in <see cref="FixedParameters"/>.</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>Parameter name -> literal value sent on every call. Never shown to the LLM.</summary>
    public Dictionary<string, string> FixedParameters { get; set; } = [];

    /// <summary>When true, a call to this tool must be approved by the operator in chat before
    /// it executes (subject to <see cref="UavOps.Agent.Options.ExecutionMode"/>). Opt-in and
    /// per-tool — which specific actions are consequential enough to warrant approval is a
    /// judgment call the config author makes.</summary>
    public bool RequiresConfirmation { get; set; }
}

/// <summary>
/// One agent: either a domain agent or the root BrainAgent.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Not read from this agent's own YAML file — deliberately populated from the
    /// <c>AgentModels</c> section of appsettings.json instead (see <c>Program.cs</c>), so which
    /// model/provider serves every agent is visible and editable in one place rather than
    /// scattered across each agent's file. Null means the app-wide Ollama default.</summary>
    public string? Model { get; set; }

    /// <summary>Which backend <see cref="Model"/> is resolved against — "OpenAI" (any OpenAI-
    /// compatible endpoint, see <see cref="OpenAiOptions"/>), or omitted/null (the default) for
    /// the local Ollama endpoint. Same <c>AgentModels</c>-appsettings.json origin as
    /// <see cref="Model"/>, not YAML. Selected per agent, never globally, since a hosted
    /// provider's request limits (e.g. a free-tier daily cap) make it unsuitable as the app-wide
    /// default.</summary>
    public string? Provider { get; set; }

    public string Instructions { get; set; } = "";

    /// <summary>Shown to a parent agent as this agent's tool description when it is one of that
    /// parent's delegates, and embedded for retrieval ranking (see <see cref="Children"/>).</summary>
    public string? Description { get; set; }

    /// <summary>An example chat utterance an operator could type that would plausibly cause a
    /// parent to delegate to this agent — shown on hover in the agent-graph UI (see
    /// <see cref="AgentGraphProjector"/>). Required (validated non-blank) for every agent except
    /// the root <c>BrainAgent</c>, same exemption as <see cref="Description"/>.</summary>
    public string? ExampleUtterance { get; set; }

    /// <summary>Optional explicit, ordered list of this agent's delegates. When present (even as
    /// an empty list), this is used verbatim instead of embedding retrieval — for structural/
    /// safety-relevant branches (e.g. live vs. simulated) where the split must be guaranteed, not
    /// similarity-ranked. Omit the YAML key entirely to keep using retrieval
    /// (<c>Agents.AgentRetrievalIndex</c>) as before.</summary>
    public List<string>? Children { get; set; }

    /// <summary>Sampling temperature for this agent's model calls. Lower values (e.g. 0.1-0.3)
    /// make tool-calling decisions more consistent across repeated identical requests — a small
    /// model at default temperature will non-deterministically vary which tools it calls, in
    /// what order, and whether it batches them into one turn or spreads them across several.
    /// Null uses the provider's default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Operation-backed tools this agent may call.</summary>
    public List<AgentToolConfig> Tools { get; set; } = [];
}

public enum ExecutionMode
{
    Direct,
    Confirm
}

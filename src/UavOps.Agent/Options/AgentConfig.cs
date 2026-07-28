namespace UavOps.Agent.Options;

/// <summary>
/// One tool an agent may call. <see cref="Operation"/> must match a method name on
/// <see cref="UavOps.Agent.Operations.IOperationService"/> exactly — that interface supplies the
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
/// One agent: either a domain agent or the MainAgent.
/// </summary>
public sealed class AgentConfig
{
    public string? Model { get; set; }
    public string Instructions { get; set; } = "";

    /// <summary>Shown to MainAgent as this agent's tool description when it is one of MainAgent's delegates.</summary>
    public string? Description { get; set; }

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

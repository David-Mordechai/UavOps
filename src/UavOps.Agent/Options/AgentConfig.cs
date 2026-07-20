namespace UavOps.Agent.Options;

/// <summary>
/// One tool an agent may call. <see cref="OperationId"/> links to the OpenAPI operation that
/// supplies the mechanical HTTP contract (verb, path, parameter types). Everything the LLM
/// actually reads — <see cref="Description"/> and every parameter description — is authored
/// here, never taken from the upstream spec.
/// </summary>
public sealed class AgentToolConfig
{
    public required string OperationId { get; init; }
    public required string Description { get; init; }

    /// <summary>Parameter name -> description shown to the LLM. Must cover every parameter
    /// the operation requires that isn't listed in <see cref="FixedParameters"/>.</summary>
    public Dictionary<string, string> Parameters { get; init; } = [];

    /// <summary>Parameter name -> literal value sent on every call. Never shown to the LLM.</summary>
    public Dictionary<string, string> FixedParameters { get; init; } = [];
}

/// <summary>
/// One agent: either a domain agent (has <see cref="Tools"/>) or the MainAgent
/// (has <see cref="Delegates"/> — the agent-as-tool / handoff list).
/// </summary>
public sealed class AgentConfig
{
    public string? Model { get; init; }
    public required string Instructions { get; init; }

    /// <summary>Shown to MainAgent as this agent's tool description when it is one of MainAgent's delegates.</summary>
    public string? Description { get; init; }

    /// <summary>Sampling temperature for this agent's model calls. Lower values (e.g. 0.1-0.3)
    /// make tool-calling decisions more consistent across repeated identical requests — a small
    /// model at default temperature will non-deterministically vary which tools it calls, in
    /// what order, and whether it batches them into one turn or spreads them across several.
    /// Null uses the provider's default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Names of other agents this agent may delegate to (MainAgent only).</summary>
    public List<string> Delegates { get; init; } = [];

    /// <summary>OpenAPI-backed tools this agent may call (domain agents only).</summary>
    public List<AgentToolConfig> Tools { get; init; } = [];
}

public enum ExecutionMode
{
    Direct,
    Confirm
}

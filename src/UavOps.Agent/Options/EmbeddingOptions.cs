namespace UavOps.Agent.Options;

/// <summary>
/// A dedicated OpenAI-compatible embeddings endpoint, separate from <see cref="OpenAiOptions"/>
/// (the chat completion endpoint) — typically a different local vLLM instance/port serving an
/// embedding-only model (e.g. <c>Qwen/Qwen3-Embedding-8B</c>, <c>--runner pooling</c>), not the
/// chat model. Load-bearing for <see cref="Tooling.ToolRetrievalIndex"/>'s tool-selection ranking —
/// see that class's own doc comment for why a real semantic embedding model is required here, not
/// an offline/hash-based fake.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public required string Endpoint { get; init; }
    public required string Model { get; init; }
}

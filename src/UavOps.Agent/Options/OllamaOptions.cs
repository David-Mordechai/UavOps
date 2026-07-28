namespace UavOps.Agent.Options;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public required string Endpoint { get; init; }
    public required string DefaultModel { get; init; }
    public required string EmbeddingModel { get; init; }
}

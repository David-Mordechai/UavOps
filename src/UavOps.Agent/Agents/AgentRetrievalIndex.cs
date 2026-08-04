using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using UavOps.Agent.Options;

namespace UavOps.Agent.Agents;

public sealed class AgentRetrievalIndex
{
    private readonly Dictionary<string, Embedding<float>> _index;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    private AgentRetrievalIndex(
        Dictionary<string, Embedding<float>> index,
        IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _index = index;
        _generator = generator;
    }

    public int Count => _index.Count;

    /// <summary>
    /// Builds the AgentRetrievalIndex once at startup.
    /// Excludes BrainAgent from the index as it's never a valid delegate target.
    /// </summary>
    public static async Task<AgentRetrievalIndex> BuildAsync(
        Dictionary<string, AgentConfig> agents,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, Embedding<float>>();

        foreach (var (name, config) in agents)
        {
            if (name == "BrainAgent")
            {
                continue;
            }

            var description = config.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                // This shouldn't happen due to AgentConfigValidator's startup check,
                // but if it does, skip to avoid generating useless embeddings.
                continue;
            }

            var response = await generator.GenerateAsync([description], cancellationToken: cancellationToken);
            var embedding = response.FirstOrDefault() 
                ?? throw new InvalidOperationException($"No embedding returned for agent '{name}'.");

            index[name] = embedding;
        }

        return new AgentRetrievalIndex(index, generator);
    }

    /// <summary>
    /// Generates an embedding vector for the query text.
    /// </summary>
    public async Task<Embedding<float>> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        var response = await _generator.GenerateAsync([text], cancellationToken: cancellationToken);
        return response.FirstOrDefault() 
            ?? throw new InvalidOperationException("Failed to generate embedding for the query.");
    }

    /// <summary>
    /// Ranks all indexed agents by cosine similarity to `query`, excluding anything in `visited`
    /// (self + every ancestor in the delegation chain), returns the top `topK` names, highest similarity first.
    /// </summary>
    public IReadOnlyList<string> RankCandidates(Embedding<float> query, IReadOnlySet<string> visited, int topK)
    {
        var list = new List<(string Name, float Similarity)>();

        foreach (var (name, embedding) in _index)
        {
            if (visited.Contains(name))
            {
                continue;
            }

            var similarity = CosineSimilarity(query.Vector, embedding.Vector);
            list.Add((name, similarity));
        }

        return list
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .Select(x => x.Name)
            .ToList();
    }

    private static float CosineSimilarity(ReadOnlyMemory<float> vectorA, ReadOnlyMemory<float> vectorB)
    {
        var spanA = vectorA.Span;
        var spanB = vectorB.Span;

        if (spanA.Length != spanB.Length)
        {
            throw new ArgumentException($"Vector lengths must match. Left: {spanA.Length}, Right: {spanB.Length}");
        }

        double dotProduct = 0.0;
        double normA = 0.0;
        double normB = 0.0;

        for (int i = 0; i < spanA.Length; i++)
        {
            float a = spanA[i];
            float b = spanB[i];

            dotProduct += a * b;
            normA += a * a;
            normB += b * b;
        }

        if (normA == 0.0 || normB == 0.0)
        {
            return 0.0f;
        }

        return (float)(dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }
}

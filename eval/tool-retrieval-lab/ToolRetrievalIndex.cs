// Tool-level counterpart to UavOps.Agent's already-proven Agents/AgentRetrievalIndex.cs (embed
// once, brute-force cosine similarity, top-K) - same shape, adapted to rank tools instead of
// agents. No vector database: at up to ~1000 tools this is a sub-millisecond in-process scan, no
// different in kind from what the real app's agent-retrieval index already does at smaller scale.
// Requires REAL semantic embeddings (Ollama nomic-embed-text) - a hash-based fake embedding
// (the real app's InMemoryEmbeddingGenerator) would rank candidates uncorrelated with meaning,
// which defeats the entire point of what's being tested here.
using Microsoft.Extensions.AI;

sealed class ToolRetrievalIndex
{
    private readonly List<(AITool Tool, Embedding<float> Embedding)> _index;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    private ToolRetrievalIndex(List<(AITool, Embedding<float>)> index, IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _index = index;
        _generator = generator;
    }

    public int Count => _index.Count;

    private const int BatchSize = 64;

    public static async Task<ToolRetrievalIndex> BuildAsync(
        IReadOnlyList<AITool> tools, IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        // Batched (many texts per HTTP call), not one call per tool - at ~1000 tools, one-at-a-time
        // calls took 25+ minutes; batching turns this into ~16 round trips instead of ~1000.
        var index = new List<(AITool, Embedding<float>)>();
        foreach (var batch in tools.Chunk(BatchSize))
        {
            // Embed name + description together - a bare description like "Change a UAV's target
            // cruise speed" loses the "SetSpeed" identifier a real operator utterance's wording
            // often echoes back (e.g. "set speed to 250").
            var texts = batch.Select(tool => $"{tool.Name}: {tool.Description}").ToList();
            var response = await generator.GenerateAsync(texts);
            if (response.Count != batch.Length)
            {
                throw new InvalidOperationException($"Expected {batch.Length} embeddings, got {response.Count}.");
            }
            for (var i = 0; i < batch.Length; i++)
            {
                index.Add((batch[i], response[i]));
            }
        }
        return new ToolRetrievalIndex(index, generator);
    }

    public async Task<Embedding<float>> EmbedQueryAsync(string text)
    {
        var response = await _generator.GenerateAsync([text]);
        return response.FirstOrDefault() ?? throw new InvalidOperationException("Failed to embed query.");
    }

    /// <summary>Ranks every indexed tool by cosine similarity to <paramref name="query"/>, returns
    /// the top <paramref name="topK"/> tools plus each tool's (name, score) for logging - highest
    /// similarity first. Fixed top-K for now, not adaptive - see the lab's own plan doc for why.
    /// Mirrors the real app's own <c>Tooling.ToolRetrievalIndex.RankCandidates</c> -
    /// <paramref name="maxScoreGapFromBest"/> is the same "don't pad a weak candidate into an
    /// empty slot" cutoff, measured here against this lab's own scenario set before being trusted
    /// as the real app's default.</summary>
    public (List<AITool> Tools, List<(string Name, float Score)> Ranked) RankCandidates(
        Embedding<float> query, int topK, IReadOnlySet<string>? enabledNames = null, float? maxScoreGapFromBest = null)
    {
        var scored = _index
            .Select(entry => (entry.Tool.Name, Score: CosineSimilarity(query.Vector, entry.Embedding.Vector), entry.Tool))
            .ToList();

        var minAcceptableScore = maxScoreGapFromBest is { } gap && scored.Count > 0
            ? scored.Max(x => x.Score) - gap
            : float.NegativeInfinity;

        var top = scored
            .Where(x => enabledNames is null || enabledNames.Contains(x.Name))
            .Where(x => x.Score >= minAcceptableScore)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
        return (top.Select(x => x.Tool).ToList(), top.Select(x => (x.Name, x.Score)).ToList());
    }

    /// <summary>Full ranked list (every indexed tool, highest similarity first) - diagnostic only,
    /// to see exactly how far down a specific tool falls, not used by the normal retrieval path.</summary>
    public List<(string Name, float Score)> RankAll(Embedding<float> query) =>
        _index
            .Select(entry => (entry.Tool.Name, Score: CosineSimilarity(query.Vector, entry.Embedding.Vector)))
            .OrderByDescending(x => x.Score)
            .ToList();

    private static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < spanA.Length; i++)
        {
            dot += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }
        if (normA == 0 || normB == 0) return 0f;
        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }
}

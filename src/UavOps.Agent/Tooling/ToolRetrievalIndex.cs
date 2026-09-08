using Microsoft.Extensions.AI;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Ranks BrainAgent's full tool catalog by real semantic similarity to the operator's turn text,
/// narrowing what actually gets offered to the model each turn — ported from and validated against
/// the standalone <c>eval/tool-retrieval-lab/</c> lab this session (7 scenarios across 3 real
/// domains — fleet, watchdog, simulator — at up to 1000 synthetic tools, 100% pass rate, real
/// recall@K measurements). Same shape as the now-deleted agent-selection <c>AgentRetrievalIndex</c>
/// (embed once, brute-force cosine similarity, top-K) — no vector database: at the real tool counts
/// this app has (dozens today, the lab validated up to 1000), an in-process linear scan is
/// sub-millisecond, and introducing a dedicated ANN index would be complexity with no measured
/// benefit at this scale.
///
/// Requires REAL semantic embeddings (<see cref="Options.EmbeddingOptions"/>, a
/// <c>Qwen/Qwen3-Embedding-8B</c>-class model) — a hash-based fake embedding would rank tools
/// uncorrelated with meaning, which is actively dangerous here: a real operation silently missing
/// from a turn's narrowed candidate set is worse than a loud startup failure, since ranking quality
/// is now load-bearing for tool-call correctness on every real request, not just a nice-to-have.
///
/// The lab found embedding just a bare description can under-rank a real prerequisite tool whose
/// own wording never mentions the domain it's needed for (e.g. a VM-host-startup tool with no
/// "simulator" keyword in its description, for a query about "the simulator") — the fix was adding
/// the missing context to the tool's own <see cref="AgentToolConfig"/>-authored description, not a
/// change to this class; keep that in mind when a real tool seems to rank lower than expected.
/// </summary>
public sealed class ToolRetrievalIndex
{
    private const int BatchSize = 64;

    private readonly List<(string Name, Embedding<float> Embedding)> _index;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    private ToolRetrievalIndex(List<(string, Embedding<float>)> index, IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _index = index;
        _generator = generator;
    }

    public int Count => _index.Count;

    /// <summary>Builds the index once at startup from a template tool list (stable
    /// (Name, Description) pairs only — the real, correlationId-scoped tool instances used to
    /// actually invoke a call are built fresh per turn by <see cref="AgentFactory"/> and looked up
    /// by name against <see cref="RankCandidates"/>'s result, not held here).</summary>
    public static async Task<ToolRetrievalIndex> BuildAsync(
        IReadOnlyList<AITool> templateTools, IEmbeddingGenerator<string, Embedding<float>> generator, CancellationToken cancellationToken)
    {
        // Batched (many texts per HTTP call), not one call per tool - a one-at-a-time loop over
        // ~1000 tools took 25+ minutes in the lab; batching turns this into a handful of round trips.
        var index = new List<(string, Embedding<float>)>();
        foreach (var batch in templateTools.Chunk(BatchSize))
        {
            // Embed name + description together - a bare description like "Change a UAV's target
            // cruise speed" loses the "SetSpeed" identifier a real operator utterance's wording
            // often echoes back (e.g. "set speed to 250").
            var texts = batch.Select(tool => $"{tool.Name}: {tool.Description}").ToList();
            var response = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
            if (response.Count != batch.Length)
            {
                throw new InvalidOperationException($"Expected {batch.Length} embeddings, got {response.Count}.");
            }
            for (var i = 0; i < batch.Length; i++)
            {
                index.Add((batch[i].Name, response[i]));
            }
        }
        return new ToolRetrievalIndex(index, generator);
    }

    public async Task<Embedding<float>> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        var response = await _generator.GenerateAsync([text], cancellationToken: cancellationToken);
        return response.FirstOrDefault() ?? throw new InvalidOperationException("Failed to embed query.");
    }

    /// <summary>Ranks every indexed tool name by cosine similarity to <paramref name="query"/>,
    /// returns the top <paramref name="topK"/> names, highest similarity first. Fixed top-K, not
    /// adaptive — see this class's own doc comment; the lab deliberately kept this simple and it
    /// held up at every scale tested.</summary>
    public List<string> RankCandidates(Embedding<float> query, int topK) =>
        _index
            .Select(entry => (entry.Name, Score: CosineSimilarity(query.Vector, entry.Embedding.Vector)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Name)
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

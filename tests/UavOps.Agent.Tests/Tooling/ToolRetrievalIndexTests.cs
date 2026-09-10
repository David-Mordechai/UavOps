using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Tooling;

public class ToolRetrievalIndexTests
{
    // Simple 2D vectors so cosine similarity is exactly predictable: identical direction -> 1.0,
    // orthogonal -> 0.0, 30 degrees apart -> cos(30 deg) ~= 0.866. Real production embeddings are
    // high-dimensional and never this clean - eval/tool-retrieval-lab is where real score behavior
    // against the real embedding model is validated (see RankCandidates's own doc comment for the
    // live-reproduced incident and the real numbers that shaped MaxScoreGapFromBest's default).
    private static readonly float[] Query = [1f, 0f];
    private static readonly float[] SameDirection = [1f, 0f]; // cos = 1.0
    private static readonly float[] Orthogonal = [0f, 1f]; // cos = 0.0
    private static readonly float[] ThirtyDegreesOff = [0.8660254f, 0.5f]; // cos ~= 0.866

    private static async Task<ToolRetrievalIndex> BuildIndexAsync(params (string Name, string ServerName, float[] Vector)[] tools)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                var embeddings = texts.Select(text =>
                {
                    var tool = tools.SingleOrDefault(t => text.StartsWith(t.Name + ":", StringComparison.Ordinal));
                    // Anything that isn't one of the indexed tools' own "Name: Description" text is
                    // the query itself, embedded via EmbedQueryAsync - always the same fixed Query
                    // vector so RankCandidates's cosine similarities match each Vector constant's
                    // own doc comment exactly.
                    return new Embedding<float>(tool.Name is null ? Query : tool.Vector);
                }).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            });

        var templateTools = tools.Select(t => (AITool)AIFunctionFactory.Create(() => "ok", name: t.Name, description: "x")).ToList();
        var toolNameToServerName = tools.ToDictionary(t => t.Name, t => t.ServerName, StringComparer.Ordinal);
        return await ToolRetrievalIndex.BuildAsync(templateTools, toolNameToServerName, generator, CancellationToken.None);
    }

    private static async Task<Embedding<float>> QueryEmbeddingAsync(ToolRetrievalIndex index) =>
        await index.EmbedQueryAsync("query", CancellationToken.None);

    [Fact]
    public async Task RankCandidates_NoGap_PadsTopKWithWeakMatchesRegardlessOfScore()
    {
        var index = await BuildIndexAsync(("Weak", "domainA", Orthogonal));
        var query = await QueryEmbeddingAsync(index);

        var results = index.RankCandidates(query, topK: 10, enabledServerNames: null, maxScoreGapFromBest: null);

        results.Should().ContainSingle(r => r.Name == "Weak");
    }

    [Fact]
    public async Task RankCandidates_TrueBestHiddenByDisabledServer_ExcludesWeakSubstitute()
    {
        // Mirrors the live-reproduced incident: a disabled server hides the real match ("Best"),
        // and without gap filtering the only enabled candidate ("Weak") would still get offered
        // purely to pad an otherwise-empty slot - MaxScoreGapFromBest exists specifically to stop
        // that.
        var index = await BuildIndexAsync(
            ("Weak", "domainA", Orthogonal),
            ("Best", "domainB", SameDirection));
        var query = await QueryEmbeddingAsync(index);

        var results = index.RankCandidates(query, topK: 10, enabledServerNames: new HashSet<string> { "domainA" }, maxScoreGapFromBest: 0.5f);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RankCandidates_LegitimateSecondaryMatchWithinMargin_IsStillOffered()
    {
        // A real multi-part request (e.g. "start the simulator and list lessons") needs more than
        // just the single top-scoring tool - live-verified via eval/tool-retrieval-lab that a
        // margin of 0.25 keeps a real secondary requirement scoring ~0.87 below the turn's own top
        // match. Modeled here as 30 degrees off the primary match (cos ~= 0.866, i.e. a ~0.13 gap).
        var index = await BuildIndexAsync(
            ("Primary", "domainA", SameDirection),
            ("Secondary", "domainA", ThirtyDegreesOff));
        var query = await QueryEmbeddingAsync(index);

        var results = index.RankCandidates(query, topK: 10, enabledServerNames: null, maxScoreGapFromBest: 0.25f);

        results.Select(r => r.Name).Should().BeEquivalentTo(["Primary", "Secondary"]);
    }

    [Fact]
    public async Task RankCandidates_GapExcludesGenuinelyWeakMatchEvenWhenNothingIsDisabled()
    {
        var index = await BuildIndexAsync(
            ("Primary", "domainA", SameDirection),
            ("Unrelated", "domainA", Orthogonal));
        var query = await QueryEmbeddingAsync(index);

        var results = index.RankCandidates(query, topK: 10, enabledServerNames: null, maxScoreGapFromBest: 0.25f);

        results.Select(r => r.Name).Should().BeEquivalentTo(["Primary"]);
    }
}

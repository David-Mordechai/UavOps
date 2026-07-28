using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using UavOps.Agent.Agents;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Agents;

public class AgentRetrievalIndexTests
{
    [Fact]
    public async Task BuildAsync_ExcludesMainAgent_AndPopulatesIndex()
    {
        // Arrange
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var list = new List<Embedding<float>> { new(new float[] { 1.0f }) };
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
            });

        var agents = new Dictionary<string, AgentConfig>
        {
            ["MainAgent"] = new AgentConfig { Instructions = "Coordinator" },
            ["FlightControlAgent"] = new AgentConfig { Instructions = "Flight", Description = "Handles flight controls" }
        };

        // Act
        var index = await AgentRetrievalIndex.BuildAsync(agents, generator, CancellationToken.None);

        // Assert
        index.Count.Should().Be(1);
    }

    [Fact]
    public async Task RankCandidates_OrdersByCosineSimilarity_ExcludesVisited_AndRespectsTopK()
    {
        // Arrange
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var values = callInfo.Arg<IEnumerable<string>>()?.ToList() ?? [];
                var list = new List<Embedding<float>>();
                foreach (var val in values)
                {
                    float[] vector = val switch
                    {
                        "Handles flight controls" => [1.0f, 0.0f, 0.0f],
                        "Handles payload settings" => [0.0f, 1.0f, 0.0f],
                        "Handles ground settings" => [0.0f, 0.0f, 1.0f],
                        _ => [0.0f, 0.0f, 0.0f]
                    };
                    list.Add(new Embedding<float>(new ReadOnlyMemory<float>(vector)));
                }
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
            });

        var agents = new Dictionary<string, AgentConfig>
        {
            ["MainAgent"] = new AgentConfig { Instructions = "Coordinator" },
            ["FlightControlAgent"] = new AgentConfig { Instructions = "Flight", Description = "Handles flight controls" },
            ["PayloadControlAgent"] = new AgentConfig { Instructions = "Payload", Description = "Handles payload settings" },
            ["GdtControlAgent"] = new AgentConfig { Instructions = "GDT", Description = "Handles ground settings" }
        };

        var index = await AgentRetrievalIndex.BuildAsync(agents, generator, CancellationToken.None);

        // Act & Assert 1: Query is highly similar to FlightControlAgent, somewhat similar to Payload, not to GDT.
        // We'll pass a custom embedding for query manually since RankCandidates takes query embedding directly.
        var queryEmbedding = new Embedding<float>(new float[] { 0.9f, 0.1f, 0.0f });

        // Rank all, topK = 5
        var rankedAll = index.RankCandidates(queryEmbedding, new HashSet<string>(), topK: 5);
        rankedAll.Should().HaveCount(3);
        rankedAll[0].Should().Be("FlightControlAgent");
        rankedAll[1].Should().Be("PayloadControlAgent");
        rankedAll[2].Should().Be("GdtControlAgent");

        // Rank with FlightControlAgent visited
        var rankedWithVisited = index.RankCandidates(queryEmbedding, new HashSet<string> { "FlightControlAgent" }, topK: 5);
        rankedWithVisited.Should().HaveCount(2);
        rankedWithVisited[0].Should().Be("PayloadControlAgent");
        rankedWithVisited[1].Should().Be("GdtControlAgent");

        // Rank with topK = 1
        var rankedTopK = index.RankCandidates(queryEmbedding, new HashSet<string>(), topK: 1);
        rankedTopK.Should().HaveCount(1);
        rankedTopK[0].Should().Be("FlightControlAgent");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents;
using Xunit;

namespace UavOps.Agent.Tests.Agents;

public class InMemoryEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_SameInputs_ProducesIdenticalVectors()
    {
        // Arrange
        var generator = new InMemoryEmbeddingGenerator();
        string text = "Handles flight controls.";

        // Act
        var result1 = await generator.GenerateAsync([text]);
        var result2 = await generator.GenerateAsync([text]);

        // Assert
        var vector1 = result1.First().Vector.ToArray();
        var vector2 = result2.First().Vector.ToArray();

        vector1.Should().Equal(vector2);
    }

    [Fact]
    public async Task GenerateAsync_DifferentInputs_ProducesDifferentVectors()
    {
        // Arrange
        var generator = new InMemoryEmbeddingGenerator();
        string text1 = "Handles flight controls.";
        string text2 = "Handles payload camera settings.";

        // Act
        var result1 = await generator.GenerateAsync([text1]);
        var result2 = await generator.GenerateAsync([text2]);

        // Assert
        var vector1 = result1.First().Vector.ToArray();
        var vector2 = result2.First().Vector.ToArray();

        vector1.Should().NotEqual(vector2);
    }

    [Fact]
    public async Task GenerateAsync_ProducesNormalizedVectors()
    {
        // Arrange
        var generator = new InMemoryEmbeddingGenerator();
        string text = "Handles flight controls.";

        // Act
        var result = await generator.GenerateAsync([text]);

        // Assert
        var vector = result.First().Vector.ToArray();
        double sumOfSquares = 0.0;
        foreach (float val in vector)
        {
            sumOfSquares += val * val;
        }

        // Sum of squares of a unit/normalized vector should be exactly 1.0 (with a tiny float epsilon)
        sumOfSquares.Should().BeApproximately(1.0, 1e-5);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace UavOps.Agent.Agents;

/// <summary>
/// A zero-dependency, local in-memory embedding generator.
/// Generates deterministic 384-dimensional vectors seeded by the input string's hashcode.
/// Extremely useful for standalone, offline development, or environments where pulling
/// embedding models is restricted or slow.
/// </summary>
public sealed class InMemoryEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public void Dispose()
    {
        // No-op
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();

        foreach (var val in values)
        {
            // Standard length for many local embedding models (like nomic-embed-text/all-minilm)
            const int VectorLength = 384;
            var vector = new float[VectorLength];

            // Use deterministic string hashing as seed so the same text always produces the exact same vector
            int seed = GetDeterministicHashCode(val);
            var rand = new Random(seed);

            // Populate normalized random floats between -1.0 and 1.0
            double sumOfSquares = 0.0;
            for (int i = 0; i < VectorLength; i++)
            {
                double valRandom = rand.NextDouble() * 2.0 - 1.0;
                vector[i] = (float)valRandom;
                sumOfSquares += valRandom * valRandom;
            }

            // Normalize the vector (unit length) for correct cosine similarity comparisons
            if (sumOfSquares > 0.0)
            {
                float length = (float)Math.Sqrt(sumOfSquares);
                for (int i = 0; i < VectorLength; i++)
                {
                    vector[i] /= length;
                }
            }

            list.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>
    /// Computes a platform-independent, stable deterministic hash code for a string.
    /// Standard string.GetHashCode() is not stable across processes or runs in .NET.
    /// </summary>
    private static int GetDeterministicHashCode(string str)
    {
        unchecked
        {
            int hash1 = (5381 << 16) + 5381;
            int hash2 = hash1;

            for (int i = 0; i < str.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i + 1 < str.Length)
                {
                    hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                }
            }

            return hash1 + (hash2 * 1566083941);
        }
    }
}

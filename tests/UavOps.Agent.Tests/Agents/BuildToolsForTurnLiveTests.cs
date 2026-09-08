using FluentAssertions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

/// <summary>
/// Direct check of the actual per-turn retrieval-selection mechanism itself (embedding-based
/// narrowing to top-K, see <see cref="AgentFactory.BuildToolsForTurn"/>), asked about directly by
/// the operator: does the tool the phrase obviously needs actually survive into the candidate set
/// offered to the model that turn, or could it be silently excluded - which would make a
/// zero-tool-call fabricated answer structurally inevitable regardless of what the model "wants"
/// to do. No LLM completion involved - only the embedding call - so this is fast and isolates
/// retrieval from model behavior.
/// </summary>
public class BuildToolsForTurnLiveTests(ITestOutputHelper output)
{
    static BuildToolsForTurnLiveTests() => LiveTestSupport.ClearProgressLogOnce();

    [Fact]
    [Trait("Category", "Live")]
    public async Task BuildToolsForTurn_IncludesTheObviouslyRelevantTool_ForThePhrasesThatFabricated()
    {
        var (_, _, _, factory, fleetClient, _, _) = await LiveTestSupport.BuildLiveOrchestrator();
        await using var _ = fleetClient;

        var cases = new (string Text, string ExpectedTool)[]
        {
            ("What UAVs do we have?", "ListFleet"),
            ("point their payloads there", "PointPayload"),
            ("fly them all to target alpha at speed 250 and altitude 3000", "Navigate"),
        };

        foreach (var (text, expectedTool) in cases)
        {
            var tools = await factory.BuildToolsForTurn($"diag-{Guid.NewGuid():N}", text, CancellationToken.None);
            var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();

            output.WriteLine($"=== \"{text}\" ===");
            output.WriteLine(string.Join(", ", names));
            output.WriteLine($"contains {expectedTool}: {names.Contains(expectedTool)}");
            output.WriteLine("");

            names.Should().Contain(expectedTool,
                $"the phrase \"{text}\" obviously needs {expectedTool}, but retrieval's top-{names.Count} candidate set excluded it");
        }
    }
}

using UavOps.Agent.Agents;
using Xunit;
using Xunit.Abstractions;

namespace UavOps.Agent.Tests.Agents;

public class GetLiveResponseTests(ITestOutputHelper output)
{
    static GetLiveResponseTests() => LiveTestSupport.ClearProgressLogOnce();

    // Requires a live Ollama/vLLM backend actually running and reachable per appsettings.json -
    // unlike the rest of this project, which `dotnet test tests/UavOps.Agent.Tests` (see
    // CLAUDE.md) documents as running fully offline. This trait doesn't itself exclude it from
    // that plain command (xunit runs everything by default) - it only makes it selectable, e.g.
    // `dotnet test tests/UavOps.Agent.Tests --filter Category=Live` or `--filter Category!=Live`.
    [Fact]
    [Trait("Category", "Live")]
    public async Task GetLiveResponse()
    {
        LiveTestSupport.LiveLog(output, "[GetLiveResponse] starting...");
        var (orchestrator, _, _, _, fleetClient, _) = await LiveTestSupport.BuildLiveOrchestrator();
        await using var _ = fleetClient;

        var (responseText, duration) = await orchestrator.HandleAsync("set speed to 250 to uav 1", "corr-123", CancellationToken.None);

        LiveTestSupport.LiveLog(output, $"[GetLiveResponse] done (duration={duration}s)");
        output.WriteLine(responseText);
    }
}

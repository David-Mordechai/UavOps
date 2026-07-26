using Xunit;

namespace UavOps.Agent.Evals;

/// <summary>
/// Checks that Ollama, UavOps.ControlApi, and UavOps.Agent are all actually running before any
/// golden-set case executes. Throwing from InitializeAsync makes xUnit fail every test in the
/// class with a clear message — this is meant to fail loudly, not skip quietly, when the
/// operator forgot to start the live dependencies this suite needs.
/// </summary>
public sealed class LiveDependenciesFixture : IAsyncLifetime
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task InitializeAsync()
    {
        await CheckReachable("http://localhost:11434/api/version", "Ollama");
        await CheckReachable("http://localhost:5250/uavs", "UavOps.ControlApi");
        await CheckReachable("http://localhost:5262/healthz", "UavOps.Agent");
    }

    private async Task CheckReachable(string url, string name)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{name} is not reachable at {url}. Start it before running these evals.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{name} responded with HTTP {(int)response.StatusCode} at {url} — is it healthy?");
        }
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }
}

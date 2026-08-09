using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UavOps.Agent.Agents.MaintenanceAgent;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class WatchdogHealthPollerTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task PollsImmediatelyOnStartup_AndUpdatesTheStore()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"Healthy","entries":{"svc-a":{"status":"Healthy"}}}""")
        });
        var store = new WatchdogHealthStore();
        var options = new WatchdogOptions { HealthCheckUrl = "http://localhost/health", PollIntervalSeconds = 60 };
        var sut = new WatchdogHealthPoller(new StubHttpClientFactory(handler), store, options, NullLogger<WatchdogHealthPoller>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => store.Current is not null, TimeSpan.FromSeconds(5));

            store.Current!.OverallStatus.Should().Be("Healthy");
            store.Current!.Services["svc-a"].Status.Should().Be("Healthy");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OneBadPoll_DoesNotStopSubsequentPolls()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"Healthy","entries":{}}""") };
        });
        var store = new WatchdogHealthStore();
        var options = new WatchdogOptions { HealthCheckUrl = "http://localhost/health", PollIntervalSeconds = 1 };
        var sut = new WatchdogHealthPoller(new StubHttpClientFactory(handler), store, options, NullLogger<WatchdogHealthPoller>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => store.Current is not null, TimeSpan.FromSeconds(10));

            store.Current!.OverallStatus.Should().Be("Healthy");
            callCount.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed >= timeout)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(20);
        }
    }
}

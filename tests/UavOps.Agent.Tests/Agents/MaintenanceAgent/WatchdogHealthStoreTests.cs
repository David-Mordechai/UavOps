using FluentAssertions;
using UavOps.Agent.McpWatchdog;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class WatchdogHealthStoreTests
{
    [Fact]
    public void Current_BeforeAnyUpdate_IsNull()
    {
        var sut = new WatchdogHealthStore();

        sut.Current.Should().BeNull();
    }

    [Fact]
    public void Update_ThenCurrent_ReturnsTheLatestSnapshot()
    {
        var sut = new WatchdogHealthStore();
        var first = new WatchdogHealthSnapshot("Healthy", new Dictionary<string, ServiceHealthEntry>(), DateTimeOffset.UtcNow, Stale: false);
        var second = new WatchdogHealthSnapshot("Degraded", new Dictionary<string, ServiceHealthEntry>(), DateTimeOffset.UtcNow.AddSeconds(3), Stale: false);

        sut.Update(first);
        sut.Update(second);

        sut.Current.Should().Be(second);
    }
}

using Microsoft.Extensions.DependencyInjection;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Watchdog.Fake;

/// <summary>The Fake backend's own IoC registration — <c>Program.cs</c> calls this one line
/// instead of registering <see cref="FakeWatchdogService"/> itself, keeping the main project from
/// needing to know its construction details.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFakeWatchdog(this IServiceCollection services)
    {
        services.AddSingleton<IWatchdogService, FakeWatchdogService>();
        return services;
    }
}

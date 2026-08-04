using Microsoft.Extensions.DependencyInjection;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Simulator.Fake;

/// <summary>The Fake backend's own IoC registration — <c>Program.cs</c> calls this one line
/// instead of registering <see cref="FakeLessonExecutor"/>/<see cref="FakeSimulatorService"/>
/// itself, keeping the main project from needing to know their construction details.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFakeSimulator(this IServiceCollection services)
    {
        services.AddSingleton<ILessonExecutor, FakeLessonExecutor>();
        services.AddSingleton<ISimulatorService, FakeSimulatorService>();
        return services;
    }
}

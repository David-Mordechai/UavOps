using Microsoft.Extensions.DependencyInjection;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Simulator.Fake;

/// <summary>The Fake backend's own IoC registration — called by <c>UavOps.Agent.McpSimulator</c>'s
/// own <c>Program.cs</c>, the only process left that needs any piece of this domain now that the
/// whole simulator domain (VM readiness, lesson listing, and the background lesson-run pipeline)
/// lives entirely in that one process. <see cref="FakeLessonExecutor"/> for the ExecuteLesson path
/// is registered separately there, alongside this.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFakeSimulatorInfra(this IServiceCollection services)
    {
        services.AddSingleton<ISimulatorInfraService, FakeSimulatorInfraService>();
        return services;
    }
}

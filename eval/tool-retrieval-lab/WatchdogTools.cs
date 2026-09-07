// Real (not no-op) watchdog tools, mirroring FleetTools/UavState's pattern - descriptions copied
// verbatim from src/UavOps.Agent/AgentsConfig/MaintenanceAgent/WatchdogServiceAgent.yaml so this
// is a genuine second domain to retrieve/act in, not just distractor noise for the fleet domain.
using System.ComponentModel;
using Microsoft.Extensions.AI;

sealed class WatchdogState
{
    public bool HealthChecked { get; set; }
    public string ServiceState { get; set; } = "Running";

    public override string ToString() => $"{{'healthChecked': {HealthChecked}, 'serviceState': '{ServiceState}'}}";
}

sealed class WatchdogTools(WatchdogState state)
{
    [Description("Returns the health status of the watchdog and every child service it supervises, plus an overall status, from the most recently polled state.")]
    public object GetServicesHealth()
    {
        state.HealthChecked = true;
        var result = new { overallStatus = "Healthy", services = new[] { new { name = "Moav.Watchdog.Service", status = "Healthy" } } };
        Console.WriteLine($"  GetServicesHealth() -> {result}");
        return result;
    }

    [Description("Starts the watchdog service itself. Does not ask for confirmation before running.")]
    public object StartService()
    {
        state.ServiceState = "Running";
        Console.WriteLine($"  StartService() -> {state}");
        return new { status = "started" };
    }

    [Description("Stops the watchdog service itself. Does not ask for confirmation before running.")]
    public object StopService()
    {
        state.ServiceState = "Stopped";
        Console.WriteLine($"  StopService() -> {state}");
        return new { status = "stopped" };
    }

    [Description("Restarts the watchdog service itself (stops it, then starts it again). Does not ask for confirmation before running.")]
    public object RestartService()
    {
        state.ServiceState = "Running";
        Console.WriteLine($"  RestartService() -> {state}");
        return new { status = "restarted" };
    }

    public AITool[] AsTools() =>
    [
        AIFunctionFactory.Create(GetServicesHealth),
        AIFunctionFactory.Create(StartService),
        AIFunctionFactory.Create(StopService),
        AIFunctionFactory.Create(RestartService),
    ];
}

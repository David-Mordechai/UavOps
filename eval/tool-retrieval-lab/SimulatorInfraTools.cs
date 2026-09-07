// Real (not no-op) simulator-infrastructure tools, mirroring FleetTools/UavState's pattern -
// descriptions copied verbatim from
// src/UavOps.Agent/AgentsConfig/SimulatorAgent/SimulatorInfrastructureAgent.yaml. Deliberately
// stops at ListSimulatorLessons - RunSimulatorLesson/AskOperatorWhichLesson need a confirmation/
// operator-prompt round trip this lab doesn't implement, out of scope for "start small".
using System.ComponentModel;
using Microsoft.Extensions.AI;

sealed class SimulatorState
{
    public bool HostRunning { get; set; }
    public bool VmRunning { get; set; }
    public bool LessonsListed { get; set; }

    public override string ToString() =>
        $"{{'hostRunning': {HostRunning}, 'vmRunning': {VmRunning}, 'lessonsListed': {LessonsListed}}}";
}

sealed class SimulatorInfraTools(SimulatorState state)
{
    [Description("Checks whether the VMware Workstation host application is running, starts it if not, and waits until it is actually ready to accept commands. Required first step before the training simulator can start.")]
    public object EnsureVmwareHostRunning()
    {
        state.HostRunning = true;
        Console.WriteLine($"  EnsureVmwareHostRunning() -> {state}");
        return new { hostRunning = true };
    }

    [Description("Checks whether the simulator virtual machine is powered on and starts it if not, makes sure all of its virtual network adapters are connected, and waits until the guest OS has actually finished booting.")]
    public object EnsureSimulatorVmRunning()
    {
        state.VmRunning = true;
        Console.WriteLine($"  EnsureSimulatorVmRunning() -> {state}");
        return new { vmRunning = true, networkAdaptersFailedToReconnect = Array.Empty<string>() };
    }

    [Description("Lists the available training lesson scripts, stored locally alongside this agent, not on the simulator VM.")]
    public object ListSimulatorLessons()
    {
        state.LessonsListed = true;
        var lessons = new[] { "network-failover", "gps-degraded", "engine-failure" };
        Console.WriteLine($"  ListSimulatorLessons() -> [{string.Join(", ", lessons)}]");
        return lessons;
    }

    public AITool[] AsTools() =>
    [
        AIFunctionFactory.Create(EnsureVmwareHostRunning),
        AIFunctionFactory.Create(EnsureSimulatorVmRunning),
        AIFunctionFactory.Create(ListSimulatorLessons),
    ];
}

namespace UavOps.Agent.Contracts;

/// <summary>
/// Which simulator backend answers simulator operations — mirrors <c>OperationBackend</c>'s role
/// for UAV operations. Chosen once at startup via the top-level <c>SimulatorBackend</c> config
/// key, independently by both processes that need it (<c>UavOps.Agent</c> for
/// <see cref="ISimulatorService"/>'s single remaining host-side operation,
/// <c>UavOps.Agent.McpSimulator</c> for <see cref="ISimulatorInfraService"/>'s three
/// MCP-exposed ones) — see each project's own <c>Program.cs</c>.
/// </summary>
public enum SimulatorBackend
{
    /// <summary>In-memory, no VMware/VM required — the default, same reasoning as
    /// <c>OperationBackend.Simulated</c> being the UAV-side default: most dev machines don't have
    /// the real dependency installed, so local dev/testing needs a zero-setup path that still
    /// exercises the full agent/chat/prompt/confirmation flow. Implemented in the shared
    /// <c>UavOps.Agent.Simulator.Fake</c> project, referenced by both processes above.</summary>
    Fake,

    /// <summary>Talks to a real local VMware Workstation install/VM (via <c>vmrun.exe</c>) and
    /// runs lesson scripts from a local folder on this same machine — see
    /// <see cref="SimulatorOptions"/> for the paths it needs.</summary>
    Real
}

/// <summary>
/// Connection details for the training-simulator environment: the local VMware Workstation
/// installation (host process + guest VM, driven via <c>vmrun.exe</c>) and the local folder of
/// PowerShell lesson scripts (run directly on this machine — no remote/SSH hop, since the lessons
/// live alongside whichever process needs them). Only meaningful when
/// <see cref="SimulatorBackend.Real"/> is selected — environment-specific, filled in per
/// deployment, not meaningful defaults.
///
/// Lives here (not in either process's own Options folder) because both
/// <c>UavOps.Agent</c> (needs <see cref="LessonsFolder"/>, to run the operator's chosen lesson
/// script) and <c>UavOps.Agent.McpSimulator</c> (needs every field, to drive VMware and list
/// lessons) bind their own copy of this same shape from their own <c>appsettings.json</c> — a
/// shared class avoids maintaining two near-identical option definitions.
/// </summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public string VmwareExecutablePath { get; set; } = "";
    public string VmwareProcessName { get; set; } = "vmware";
    public string VmrunExecutablePath { get; set; } = "";
    public string SimulatorVmxPath { get; set; } = "";

    /// <summary>vmx device names (e.g. <c>"ethernet0"</c>, <c>"ethernet1"</c>) of every virtual
    /// network adapter this VM has — VMware Workstation sometimes fails to auto-connect one of
    /// several adapters on power-on (shows as the greyed-out adapter you'd otherwise right-click
    /// → Connect in the UI). Since `vmrun` has no way to *check* a device's live connection state
    /// (confirmed against VMware's own vmrun command reference — only `connectNamedDevice` to
    /// force it, no query), <c>UavOps.Agent.McpSimulator.SimulatorInfraService.EnsureSimulatorVmRunning</c>
    /// just (re)connects every adapter listed here unconditionally after confirming the VM is
    /// running — reconnecting an already-connected adapter is a harmless no-op, same as clicking
    /// Connect on one that's already fine. Empty by default (no-op) — fill in with your VM's
    /// actual adapter names (check via `vmrun listNetworkAdapters &lt;path-to-vmx&gt;`, or by
    /// opening the .vmx file and looking for `ethernetN.present = "TRUE"` lines).</summary>
    public List<string> NetworkAdapterDeviceNames { get; set; } = [];

    /// <summary>How many times to attempt <c>connectNamedDevice</c> per adapter before giving up
    /// on it — since there's no way to query connection state, a failure might be a permanent
    /// config issue or just the adapter not being ready an instant after VM power-on; retrying
    /// distinguishes the two in the logs instead of giving up after one attempt.</summary>
    public int NetworkAdapterReconnectAttempts { get; set; } = 3;

    /// <summary>Delay between reconnect attempts for the same adapter.</summary>
    public int NetworkAdapterReconnectRetryDelaySeconds { get; set; } = 3;

    /// <summary>Local folder (on this machine) containing the <c>*.ps1</c> lesson scripts —
    /// <c>UavOps.Agent.McpSimulator</c> lists them, <c>UavOps.Agent</c> runs the one the operator
    /// chose.</summary>
    public string LessonsFolder { get; set; } = "";

    /// <summary>How long to poll for the VMware host to become responsive to `vmrun` commands
    /// before giving up.</summary>
    public int HostReadyTimeoutSeconds { get; set; } = 60;

    /// <summary>How long to poll for the guest VM to finish booting — VMware Tools reporting
    /// "running" — before giving up. Guest boot can genuinely take a couple of minutes, hence the
    /// longer default than <see cref="HostReadyTimeoutSeconds"/>.</summary>
    public int VmToolsReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>How often to re-check readiness while polling (both waits above).</summary>
    public int ReadyPollIntervalSeconds { get; set; } = 2;
}

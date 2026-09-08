namespace UavOps.Agent.McpSimulator;

/// <summary>Talks to the local VMware Workstation installation. Behind an interface so
/// <see cref="SimulatorInfraService"/> is unit-testable without a real VMware install.</summary>
public interface IVmwareController
{
    bool IsHostRunning();
    void StartHost();
    Task<bool> IsVmRunningAsync(CancellationToken cancellationToken);
    Task StartVmAsync(CancellationToken cancellationToken);

    /// <summary>Force-connects a named virtual device (e.g. <c>"ethernet0"</c>) — the CLI
    /// equivalent of right-clicking a greyed-out device in the VMware UI and choosing Connect.
    /// `vmrun` has no way to query a device's current connection state, so this is meant to be
    /// called unconditionally; reconnecting an already-connected device is a harmless no-op.
    /// Only works while the VM is powered on. Throws on failure.</summary>
    Task ConnectNetworkAdapterAsync(string deviceName, CancellationToken cancellationToken);

    /// <summary>Polls until `vmrun list` succeeds (proxy for "the VMware backend service is
    /// responsive," since there's no dedicated host-readiness command) or <paramref
    /// name="timeout"/> elapses. Returns whether it became ready in time.</summary>
    Task<bool> WaitForHostReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Polls `vmrun checkToolsState &lt;vmx&gt;` until it reports <c>"running"</c> (the
    /// guest OS has booted far enough for VMware Tools to start) or <paramref name="timeout"/>
    /// elapses. Deliberately does NOT use `vmrun getGuestIPAddress -wait` — VMware's own docs say
    /// that command returns immediately if the network isn't ready yet rather than actually
    /// waiting, and it's documented as unreliable specifically in the `nogui`/headless mode this
    /// controller always starts VMs with. Returns whether Tools came up in time.</summary>
    Task<bool> WaitForVmToolsRunningAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

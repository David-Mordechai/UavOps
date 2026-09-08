using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Implements <see cref="ISimulatorInfraService"/> on top of <see cref="IVmwareController"/>
/// (local VMware Workstation host/guest) and <see cref="ILessonLister"/> (lesson scripts stored
/// locally on this machine). Every method wraps its work in try/catch and returns
/// <see cref="OperationResult.Fail"/>/<c>ErrorMessage</c> on failure — never throws — the same
/// convention the rest of this codebase's operation implementations follow.
/// </summary>
public sealed class SimulatorInfraService(
    IVmwareController vmwareController,
    ILessonLister lessonLister,
    SimulatorOptions options,
    ILogger<SimulatorInfraService> logger) : ISimulatorInfraService
{
    public Task<OperationResult> EnsureVmwareHostRunning(CancellationToken cancellationToken) =>
        RunAsync("EnsureVmwareHostRunning", async () =>
        {
            var alreadyRunning = vmwareController.IsHostRunning();
            if (!alreadyRunning)
            {
                vmwareController.StartHost();
            }

            // Starting the process doesn't mean it's ready to accept vmrun commands yet — poll
            // until `vmrun list` actually succeeds (or an already-running host confirms it's
            // still responsive) rather than declaring victory the instant Process.Start returns.
            var ready = await vmwareController.WaitForHostReadyAsync(
                TimeSpan.FromSeconds(options.HostReadyTimeoutSeconds), cancellationToken);

            if (!ready)
            {
                return OperationResult.Fail(OperationError.Timeout,
                    $"VMware host did not become ready within {options.HostReadyTimeoutSeconds}s.");
            }

            return OperationResult.Ok(new { started = !alreadyRunning, alreadyRunning, ready = true });
        });

    public Task<OperationResult> EnsureSimulatorVmRunning(CancellationToken cancellationToken) =>
        RunAsync("EnsureSimulatorVmRunning", async () =>
        {
            var alreadyRunning = await vmwareController.IsVmRunningAsync(cancellationToken);
            if (!alreadyRunning)
            {
                await vmwareController.StartVmAsync(cancellationToken);
            }

            // Reconnect network adapters right after power-on, *before* waiting on guest
            // readiness below — connectNamedDevice only needs the VM powered on at the
            // hypervisor level, not a booted guest OS, and doing this first means a flaky
            // management NIC can't itself block/skew a later network-dependent readiness check.
            //
            // vmrun has no way to check a virtual network adapter's live connection state (only
            // to force-connect one), so every configured adapter is reconnected unconditionally
            // every time — harmless no-op for ones already fine, fixes the ones that silently
            // failed to auto-connect on power-on. Best-effort per adapter: one failing shouldn't
            // fail the whole "is the VM ready" result.
            //
            // Each adapter gets up to NetworkAdapterReconnectAttempts tries with a delay between
            // them — observed in production: two adapters failed with exit -1 on the only attempt
            // made, and there was no way to tell from a single data point whether that's a
            // permanent config issue or the adapter just not being ready an instant after
            // power-on. Retrying (and logging every attempt individually) turns that ambiguity
            // into something visible in the logs: consistent failure across all attempts points to
            // a real config problem, a later attempt succeeding points to a timing race.
            var reconnected = new List<string>();
            var failedToReconnect = new List<string>();
            foreach (var device in options.NetworkAdapterDeviceNames)
            {
                var succeeded = false;
                for (var attempt = 1; attempt <= options.NetworkAdapterReconnectAttempts; attempt++)
                {
                    try
                    {
                        await vmwareController.ConnectNetworkAdapterAsync(device, cancellationToken);
                        succeeded = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Adapter {Device} reconnect attempt {Attempt}/{MaxAttempts} failed",
                            device, attempt, options.NetworkAdapterReconnectAttempts);

                        if (attempt < options.NetworkAdapterReconnectAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(options.NetworkAdapterReconnectRetryDelaySeconds), cancellationToken);
                        }
                    }
                }

                if (succeeded)
                {
                    reconnected.Add(device);
                }
                else
                {
                    logger.LogWarning("Adapter {Device} failed to reconnect after {MaxAttempts} attempt(s)",
                        device, options.NetworkAdapterReconnectAttempts);
                    failedToReconnect.Add(device);
                }
            }

            // "Powered on" (vmrun list) happens almost immediately and says nothing about
            // whether the guest OS has actually finished booting. Poll VMware Tools' reported
            // state instead of vmrun's `getGuestIPAddress -wait`, which VMware's own docs say
            // returns immediately (not actually waiting) if the network isn't ready yet, and
            // which is documented as unreliable specifically in the nogui/headless mode this VM
            // is always started with.
            var toolsRunning = await vmwareController.WaitForVmToolsRunningAsync(
                TimeSpan.FromSeconds(options.VmToolsReadyTimeoutSeconds), cancellationToken);

            if (!toolsRunning)
            {
                return OperationResult.Fail(OperationError.Timeout,
                    $"Simulator VM did not finish booting (VMware Tools never reported 'running') within {options.VmToolsReadyTimeoutSeconds}s.");
            }

            return OperationResult.Ok(new
            {
                started = !alreadyRunning,
                alreadyRunning,
                networkAdaptersReconnected = reconnected,
                networkAdaptersFailedToReconnect = failedToReconnect,
                guestReady = true
            });
        });

    public Task<OperationResult> ListSimulatorLessons(CancellationToken cancellationToken) =>
        RunAsync("ListSimulatorLessons", () => Task.FromResult(OperationResult.Ok(lessonLister.ListLessons().ToList())));

    private async Task<OperationResult> RunAsync(string operationName, Func<Task<OperationResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Simulator infra operation {Operation} failed", operationName);
            return OperationResult.Fail(OperationError.ClientReportedError, ex.Message);
        }
    }
}

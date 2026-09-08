using System.Runtime.Versioning;
using System.ServiceProcess;

namespace UavOps.Agent.McpWatchdog;

/// <summary>
/// Controls a local Windows service via the Service Control Manager
/// (<see cref="System.ServiceProcess.ServiceController"/>). That API is synchronous/blocking (no
/// async overloads), so every operation runs on a thread-pool thread via <see cref="Task.Run(Action)"/>.
/// Each call waits for the expected terminal status (up to
/// <see cref="WatchdogOptions.ServiceControlTimeoutSeconds"/>) rather than returning the instant
/// Start()/Stop() returns, which — like <c>vmrun start</c> for the simulator VM — doesn't mean the
/// service has actually reached that state yet. Only ever registered under
/// <c>WatchdogBackend.Real</c>, and this whole app already assumes Windows (VMware/PowerShell),
/// so this class is explicitly marked Windows-only rather than the project targeting
/// <c>net8.0-windows</c> everywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController(WatchdogOptions options) : IWindowsServiceController
{
    public Task Start(string serviceName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var controller = new ServiceController(serviceName);
            if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                return;
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, Timeout());
        }, cancellationToken);

    public Task Stop(string serviceName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var controller = new ServiceController(serviceName);
            if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                return;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout());
        }, cancellationToken);

    public Task Restart(string serviceName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var controller = new ServiceController(serviceName);
            var timeout = Timeout();

            if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }

            controller.Refresh();
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }, cancellationToken);

    private TimeSpan Timeout() => TimeSpan.FromSeconds(Math.Max(1, options.ServiceControlTimeoutSeconds));
}

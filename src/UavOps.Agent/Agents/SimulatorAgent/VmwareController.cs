using System.Diagnostics;
using System.Text;
using UavOps.Agent.Options;

namespace UavOps.Agent.Agents.SimulatorAgent;

/// <summary>
/// Checks/starts the VMware Workstation host application via <see cref="Process"/>, and
/// checks/starts the simulator guest VM by shelling out to `vmrun.exe` (VMware's own CLI — there
/// is no VMware .NET SDK).
/// </summary>
public sealed class VmwareController(SimulatorOptions options, ILogger<VmwareController> logger) : IVmwareController
{
    public bool IsHostRunning() => Process.GetProcessesByName(options.VmwareProcessName).Length > 0;

    public void StartHost()
    {
        logger.LogInformation("Starting VMware Workstation host: {Path}", options.VmwareExecutablePath);
        Process.Start(new ProcessStartInfo(options.VmwareExecutablePath) { UseShellExecute = true });
    }

    public async Task<bool> IsVmRunningAsync(CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunVmrunAsync("list", "", cancellationToken);
        return exitCode == 0 && stdout.Contains(options.SimulatorVmxPath, StringComparison.OrdinalIgnoreCase);
    }

    public async Task StartVmAsync(CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await RunVmrunAsync("start", $"\"{options.SimulatorVmxPath}\" nogui", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"vmrun start failed (exit {exitCode}): {FailureDetails(stdout, stderr)}");
        }
    }

    public async Task ConnectNetworkAdapterAsync(string deviceName, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await RunVmrunAsync("connectNamedDevice", $"\"{options.SimulatorVmxPath}\" {deviceName}", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"vmrun connectNamedDevice '{deviceName}' failed (exit {exitCode}): {FailureDetails(stdout, stderr)}");
        }
    }

    /// <summary>vmrun doesn't consistently write failure details to stderr — a real
    /// connectNamedDevice failure was observed with a nonzero exit code and completely empty
    /// stderr, silently discarding whatever it actually wrote to stdout instead. Prefer stderr
    /// when it has content, but fall back to stdout (or a plain "(no output)" marker) rather than
    /// risk swallowing the only diagnostic text vmrun produced.</summary>
    private static string FailureDetails(string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return stderr.Trim();
        }

        return string.IsNullOrWhiteSpace(stdout) ? "(no output)" : stdout.Trim();
    }

    public Task<bool> WaitForHostReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        PollUntilAsync(async ct =>
        {
            var (exitCode, _, _) = await RunVmrunAsync("list", "", ct);
            return exitCode == 0;
        }, timeout, cancellationToken);

    public Task<bool> WaitForVmToolsRunningAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        PollUntilAsync(async ct =>
        {
            var (exitCode, stdout, _) = await RunVmrunAsync("checkToolsState", $"\"{options.SimulatorVmxPath}\"", ct);
            return exitCode == 0 && string.Equals(stdout.Trim(), "running", StringComparison.OrdinalIgnoreCase);
        }, timeout, cancellationToken);

    private async Task<bool> PollUntilAsync(Func<CancellationToken, Task<bool>> check, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.ReadyPollIntervalSeconds));
        var sw = Stopwatch.StartNew();

        while (true)
        {
            if (await check(cancellationToken))
            {
                return true;
            }

            if (sw.Elapsed >= timeout)
            {
                return false;
            }

            var remaining = timeout - sw.Elapsed;
            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken);
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunVmrunAsync(string subcommand, string extraArgs, CancellationToken cancellationToken)
    {
        var arguments = string.IsNullOrEmpty(extraArgs) ? subcommand : $"{subcommand} {extraArgs}";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(options.VmrunExecutablePath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

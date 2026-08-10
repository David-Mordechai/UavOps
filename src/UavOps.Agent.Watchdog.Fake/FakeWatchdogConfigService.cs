using System.Collections.Concurrent;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Watchdog.Fake;

/// <summary>
/// In-memory <see cref="IWatchdogConfigService"/> — no real config files involved. Selected via
/// <c>WatchdogBackend: Fake</c> (the default), the same reasoning every other Fake implementation
/// in this repo exists for: exercises the full agent/chat/tool/confirmation flow (including
/// placeholder-executable inference — see <see cref="ServiceConfigNaming"/>) with nothing
/// installed. Deliberately performs **no real filesystem existence check** on any executable path
/// (there's nothing real to check against in a zero-dependency dev environment) — always treats a
/// resolved path as valid, unlike the Real backend's <c>IExecutablePathResolver</c>.
/// </summary>
public sealed class FakeWatchdogConfigService : IWatchdogConfigService
{
    private readonly ConcurrentDictionary<string, List<ServiceConfigEntry>> _configurations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flight"] =
        [
            new ServiceConfigEntry { Description = "Service One", Executable = @"%MoavProducts%\Services\ServiceOne\ServiceOne.exe" },
            new ServiceConfigEntry
            {
                Description = "Service Two",
                Executable = @"%MoavProducts%\Services\ServiceTwo\ServiceTwo.exe",
                Args = ["-c arg1"],
                HealthEndPoint = "http://localhost:1111/_health"
            }
        ],
        ["Simulator"] =
        [
            new ServiceConfigEntry { Description = "Sim Console", Executable = @"%MoavProducts%\Services\SimConsole\SimConsole.exe" }
        ]
    };

    public Task<OperationResult> ListConfigurations(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok(_configurations.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()));

    public Task<OperationResult> ListConfiguredServices(string configurationName, CancellationToken cancellationToken)
    {
        if (!_configurations.TryGetValue(configurationName, out var entries))
        {
            return Task.FromResult(OperationResult.Invalid($"Unknown configuration '{configurationName}'."));
        }

        return Task.FromResult(OperationResult.Ok(entries));
    }

    public Task<OperationResult> AddConfiguredService(
        string configurationName,
        string description,
        string? executable,
        List<string>? args,
        string? id,
        bool? disabled,
        int? retries,
        bool? isManaged,
        string? healthEndPoint,
        string? group,
        CancellationToken cancellationToken)
    {
        if (!_configurations.TryGetValue(configurationName, out var entries))
        {
            return Task.FromResult(OperationResult.Invalid($"Unknown configuration '{configurationName}'."));
        }

        if (entries.Any(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(OperationResult.Invalid(
                $"A service named '{description}' already exists in configuration '{configurationName}'."));
        }

        string resolvedExecutable;
        if (executable is not null)
        {
            resolvedExecutable = executable;
        }
        else if (ServiceConfigNaming.TryInferExecutable(entries, description, out var inferred))
        {
            resolvedExecutable = inferred!;
        }
        else
        {
            return Task.FromResult(OperationResult.Invalid(
                $"No executable path was given for '{description}', and no consistent naming pattern could be inferred " +
                $"from the other services already configured in '{configurationName}' — please provide the executable path explicitly."));
        }

        var entry = new ServiceConfigEntry
        {
            Description = description,
            Executable = resolvedExecutable,
            Args = args,
            Id = id,
            Retries = retries,
            IsManaged = isManaged,
            HealthEndPoint = healthEndPoint,
            Group = group
        };

        ServiceConfigEntryMutation.ApplyDisabled(entry, disabled);

        entries.Add(entry);

        return Task.FromResult(OperationResult.Ok(new { added = entry, yaml = ServiceConfigEntryFormatter.Format(entry) }));
    }

    public Task<OperationResult> UpdateConfiguredService(
        string configurationName,
        string description,
        string? newDescription,
        string? executable,
        List<string>? args,
        string? id,
        bool? disabled,
        int? retries,
        bool? isManaged,
        string? healthEndPoint,
        string? group,
        CancellationToken cancellationToken)
    {
        if (!_configurations.TryGetValue(configurationName, out var entries))
        {
            return Task.FromResult(OperationResult.Invalid($"Unknown configuration '{configurationName}'."));
        }

        var existing = entries.FirstOrDefault(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Task.FromResult(OperationResult.Invalid($"No service named '{description}' exists in configuration '{configurationName}'."));
        }

        if (newDescription is not null
            && !string.Equals(newDescription, description, StringComparison.OrdinalIgnoreCase)
            && entries.Any(e => string.Equals(e.Description, newDescription, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(OperationResult.Invalid(
                $"A service named '{newDescription}' already exists in configuration '{configurationName}'."));
        }

        ServiceConfigEntryMutation.ApplyDisabled(existing, disabled);

        if (executable is not null) existing.Executable = executable;
        if (newDescription is not null) existing.Description = newDescription;
        if (args is not null) existing.Args = args;
        if (id is not null) existing.Id = id;
        if (retries is not null) existing.Retries = retries;
        if (isManaged is not null) existing.IsManaged = isManaged;
        if (healthEndPoint is not null) existing.HealthEndPoint = healthEndPoint;
        if (group is not null) existing.Group = group;

        return Task.FromResult(OperationResult.Ok(new { updated = existing, yaml = ServiceConfigEntryFormatter.Format(existing) }));
    }

    public Task<OperationResult> RemoveConfiguredService(string configurationName, string description, CancellationToken cancellationToken)
    {
        if (!_configurations.TryGetValue(configurationName, out var entries))
        {
            return Task.FromResult(OperationResult.Invalid($"Unknown configuration '{configurationName}'."));
        }

        var existing = entries.FirstOrDefault(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Task.FromResult(OperationResult.Invalid($"No service named '{description}' exists in configuration '{configurationName}'."));
        }

        entries.Remove(existing);
        return Task.FromResult(OperationResult.Ok(new { removed = description }));
    }
}

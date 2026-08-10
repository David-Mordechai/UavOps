using UavOps.Agent.Contracts;

namespace UavOps.Agent.Agents.MaintenanceAgent;

/// <summary>
/// Implements <see cref="IWatchdogConfigService"/> on top of <see cref="IServiceConfigFileStore"/>
/// (path safety, header preservation, YAML I/O) and <see cref="IExecutablePathResolver"/>
/// (placeholder expansion + existence check). Every method wraps its work in try/catch and returns
/// <see cref="OperationResult.Fail"/>/<c>ErrorMessage</c> on failure — never throws — the same
/// convention <c>SimulatorService</c>/<c>WatchdogService</c> already follow.
/// </summary>
public sealed class WatchdogConfigService(
    IServiceConfigFileStore fileStore,
    IExecutablePathResolver pathResolver,
    IServiceExecutableLocator executableLocator,
    ILogger<WatchdogConfigService> logger) : IWatchdogConfigService
{
    public Task<OperationResult> ListConfigurations(CancellationToken cancellationToken) =>
        RunAsync("ListConfigurations", async () =>
            OperationResult.Ok(await fileStore.ListConfigurationsAsync(cancellationToken)));

    public Task<OperationResult> ListConfiguredServices(string configurationName, CancellationToken cancellationToken) =>
        RunAsync("ListConfiguredServices", async () =>
            OperationResult.Ok(await fileStore.ReadAsync(configurationName, cancellationToken)));

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
        CancellationToken cancellationToken) =>
        RunAsync("AddConfiguredService", async () =>
        {
            var entries = (await fileStore.ReadAsync(configurationName, cancellationToken)).ToList();

            if (entries.Any(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Invalid($"A service named '{description}' already exists in configuration '{configurationName}'.");
            }

            string resolvedExecutable;
            if (executable is not null)
            {
                resolvedExecutable = executable;
            }
            else
            {
                var lookup = executableLocator.Locate(description);
                switch (lookup.Kind)
                {
                    case ServiceExecutableLookupKind.Resolved:
                        resolvedExecutable = lookup.ExecutablePath!;
                        break;
                    case ServiceExecutableLookupKind.Ambiguous:
                        return OperationResult.Invalid(
                            $"'{description}' matches more than one service folder under the products path: " +
                            $"{string.Join(", ", lookup.Candidates!)}. Please clarify which one you mean, or give the executable path explicitly.");
                    case ServiceExecutableLookupKind.NotFound when ServiceConfigNaming.TryInferExecutable(entries, description, out var inferred):
                        resolvedExecutable = inferred!;
                        break;
                    default:
                        return OperationResult.Invalid(
                            $"No executable path was given for '{description}', no matching service folder was found under the products path, " +
                            $"and no consistent naming pattern could be inferred from the other services already configured in '{configurationName}' " +
                            "— please provide the executable path explicitly.");
                }
            }

            var (error, note) = ValidateExecutable(resolvedExecutable);
            if (error is not null)
            {
                return error.Value;
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
            await fileStore.WriteAsync(configurationName, entries, cancellationToken);

            return OperationResult.Ok(new { added = entry, yaml = ServiceConfigEntryFormatter.Format(entry), executableNote = note });
        });

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
        CancellationToken cancellationToken) =>
        RunAsync("UpdateConfiguredService", async () =>
        {
            var entries = (await fileStore.ReadAsync(configurationName, cancellationToken)).ToList();
            var existing = entries.FirstOrDefault(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return OperationResult.Invalid($"No service named '{description}' exists in configuration '{configurationName}'.");
            }

            if (newDescription is not null
                && !string.Equals(newDescription, description, StringComparison.OrdinalIgnoreCase)
                && entries.Any(e => string.Equals(e.Description, newDescription, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Invalid($"A service named '{newDescription}' already exists in configuration '{configurationName}'.");
            }

            string? executableNote = null;
            if (executable is not null)
            {
                var (error, note) = ValidateExecutable(executable);
                if (error is not null)
                {
                    return error.Value;
                }

                existing.Executable = executable;
                executableNote = note;
            }

            ServiceConfigEntryMutation.ApplyDisabled(existing, disabled);

            if (newDescription is not null) existing.Description = newDescription;
            if (args is not null) existing.Args = args;
            if (id is not null) existing.Id = id;
            if (retries is not null) existing.Retries = retries;
            if (isManaged is not null) existing.IsManaged = isManaged;
            if (healthEndPoint is not null) existing.HealthEndPoint = healthEndPoint;
            if (group is not null) existing.Group = group;

            await fileStore.WriteAsync(configurationName, entries, cancellationToken);
            return OperationResult.Ok(new { updated = existing, yaml = ServiceConfigEntryFormatter.Format(existing), executableNote });
        });

    public Task<OperationResult> RemoveConfiguredService(string configurationName, string description, CancellationToken cancellationToken) =>
        RunAsync("RemoveConfiguredService", async () =>
        {
            var entries = (await fileStore.ReadAsync(configurationName, cancellationToken)).ToList();
            var existing = entries.FirstOrDefault(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return OperationResult.Invalid($"No service named '{description}' exists in configuration '{configurationName}'.");
            }

            entries.Remove(existing);
            await fileStore.WriteAsync(configurationName, entries, cancellationToken);
            return OperationResult.Ok(new { removed = description });
        });

    /// <summary>Resolves + validates an executable path via <see cref="IExecutablePathResolver"/>.
    /// Returns a non-null <c>Error</c> when the operation should be rejected outright (resolved
    /// but not found), or a non-null <c>Note</c> when it should proceed but the caller should be
    /// told existence couldn't be verified (unrecognized placeholder).</summary>
    private (OperationResult? Error, string? Note) ValidateExecutable(string executable)
    {
        var resolution = pathResolver.Resolve(executable);
        return resolution.Kind switch
        {
            ExecutablePathResolutionKind.Verified => (null, null),
            ExecutablePathResolutionKind.NotFound => (
                OperationResult.Invalid($"Executable '{executable}' does not exist (checked '{resolution.ExpandedPath}')."), null),
            ExecutablePathResolutionKind.Unverifiable => (
                null, $"Could not verify '{executable}' exists — unrecognized placeholder '{resolution.UnresolvedPlaceholder}'."),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution.Kind), resolution.Kind, null)
        };
    }

    private async Task<OperationResult> RunAsync(string operationName, Func<Task<OperationResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Watchdog config operation {Operation} failed", operationName);
            return OperationResult.Fail(OperationError.ClientReportedError, ex.Message);
        }
    }
}

using System.Text.RegularExpressions;
using UavOps.Agent.Options;

namespace UavOps.Agent.Agents.MaintenanceAgent;

public sealed partial class ExecutablePathResolver(WatchdogOptions options) : IExecutablePathResolver
{
    public ExecutablePathResolution Resolve(string executablePathWithPlaceholders)
    {
        var expanded = executablePathWithPlaceholders;
        foreach (var (token, value) in options.ExecutablePlaceholders)
        {
            expanded = expanded.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        }

        var remaining = PlaceholderPattern().Match(expanded);
        if (remaining.Success)
        {
            return new ExecutablePathResolution(ExecutablePathResolutionKind.Unverifiable, expanded, remaining.Value);
        }

        return File.Exists(expanded)
            ? new ExecutablePathResolution(ExecutablePathResolutionKind.Verified, expanded)
            : new ExecutablePathResolution(ExecutablePathResolutionKind.NotFound, expanded);
    }

    [GeneratedRegex(@"%[^%\s]+%")]
    private static partial Regex PlaceholderPattern();
}

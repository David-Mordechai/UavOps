using System.Text.Json;

namespace UavOps.Agent.Agents.MaintenanceAgent;

/// <summary>
/// Parses the watchdog's HTTP health-check response — the standard ASP.NET Core HealthChecks JSON
/// shape (<c>{ status, entries: { name: { status, description } } }</c>) — into a
/// <see cref="WatchdogHealthSnapshot"/>. A pure static method, deliberately not entangled with the
/// HTTP call itself, so it's unit-testable with plain JSON strings — same reasoning
/// <c>Agents.SimulatorAgent.LocalLessonExecutor</c>'s output-scanning logic is a plain method
/// separate from the process I/O that produces its input.
/// </summary>
public static class WatchdogHealthParser
{
    public static WatchdogHealthSnapshot Parse(string json, DateTimeOffset polledAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var overallStatus = GetString(root, "status") ?? "Unknown";
        var services = new Dictionary<string, ServiceHealthEntry>(StringComparer.OrdinalIgnoreCase);

        if (TryGetProperty(root, "entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in entries.EnumerateObject())
            {
                var status = GetString(entry.Value, "status") ?? "Unknown";
                var description = GetString(entry.Value, "description");
                services[entry.Name] = new ServiceHealthEntry(status, description);
            }
        }

        return new WatchdogHealthSnapshot(overallStatus, services, polledAt, Stale: false);
    }

    // Property-name lookup is case-insensitive since HealthChecks JSON writers vary between
    // camelCase and PascalCase depending on how the watchdog serializes its response.
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

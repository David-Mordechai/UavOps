namespace UavOps.Agent.Contracts;

/// <summary>Naming-convention inference for a new watchdog service config entry's
/// <c>executable</c> path, shared by the Real and Fake <c>IWatchdogConfigService</c>
/// implementations so behavior can't drift between them.</summary>
public static class ServiceConfigNaming
{
    /// <summary>Infers a placeholder executable path for a new service from the naming convention
    /// of the configuration's existing entries: if every entry whose own <see cref="ServiceConfigEntry.Executable"/>
    /// follows <c>&lt;prefix&gt;\{Name}\{Name}.exe</c> (where <c>{Name}</c> is that entry's own
    /// <see cref="ServiceConfigEntry.Description"/> with whitespace stripped) shares the same
    /// prefix, applies that same prefix to the new description. Returns <c>false</c> (never
    /// guesses further) when there are no such entries or they don't share a consistent prefix.</summary>
    public static bool TryInferExecutable(IReadOnlyList<ServiceConfigEntry> existingEntries, string description, out string? inferred)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existingEntries)
        {
            var name = RemoveWhitespace(entry.Description);
            var suffix = $@"\{name}\{name}.exe";
            if (entry.Executable.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                prefixes.Add(entry.Executable[..^suffix.Length]);
            }
        }

        if (prefixes.Count != 1)
        {
            inferred = null;
            return false;
        }

        var newName = RemoveWhitespace(description);
        inferred = $@"{prefixes.Single()}\{newName}\{newName}.exe";
        return true;
    }

    private static string RemoveWhitespace(string value) => new([.. value.Where(c => !char.IsWhiteSpace(c))]);
}

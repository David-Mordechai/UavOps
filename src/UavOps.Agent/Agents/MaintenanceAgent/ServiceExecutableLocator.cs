using UavOps.Agent.Options;

namespace UavOps.Agent.Agents.MaintenanceAgent;

public sealed class ServiceExecutableLocator(WatchdogOptions options) : IServiceExecutableLocator
{
    private const string RootPlaceholder = "%MoavProducts%";
    private const string ServicesSubfolder = "Services";

    public ServiceExecutableLookupResult Locate(string serviceNameHint)
    {
        if (!options.ExecutablePlaceholders.TryGetValue(RootPlaceholder, out var root) || string.IsNullOrWhiteSpace(root))
        {
            return new ServiceExecutableLookupResult(ServiceExecutableLookupKind.NotFound);
        }

        var servicesRoot = Path.Combine(root, ServicesSubfolder);
        if (!Directory.Exists(servicesRoot))
        {
            return new ServiceExecutableLookupResult(ServiceExecutableLookupKind.NotFound);
        }

        var folderNames = Directory.GetDirectories(servicesRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToList();

        var hint = RemoveWhitespace(serviceNameHint);

        var exact = folderNames.FirstOrDefault(name => string.Equals(RemoveWhitespace(name), hint, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new ServiceExecutableLookupResult(ServiceExecutableLookupKind.Resolved, BuildPath(exact));
        }

        var scored = folderNames
            .Select(name => (Name: name, Distance: LevenshteinDistance(hint, RemoveWhitespace(name))))
            .Where(candidate => candidate.Distance * 2 <= Math.Max(hint.Length, RemoveWhitespace(candidate.Name).Length))
            .ToList();

        if (scored.Count == 0)
        {
            return new ServiceExecutableLookupResult(ServiceExecutableLookupKind.NotFound);
        }

        var minDistance = scored.Min(candidate => candidate.Distance);
        var best = scored
            .Where(candidate => candidate.Distance == minDistance)
            .Select(candidate => candidate.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return best.Count == 1
            ? new ServiceExecutableLookupResult(ServiceExecutableLookupKind.Resolved, BuildPath(best[0]))
            : new ServiceExecutableLookupResult(ServiceExecutableLookupKind.Ambiguous, Candidates: best);
    }

    private static string BuildPath(string folderName) => $@"{RootPlaceholder}\{ServicesSubfolder}\{folderName}\{folderName}.exe";

    private static string RemoveWhitespace(string value) => new([.. value.Where(c => !char.IsWhiteSpace(c))]);

    /// <summary>Classic full-matrix edit distance (insertions/deletions/substitutions), computed
    /// case-insensitively — the "letter by letter" comparison used to match an inexact operator-given
    /// name (e.g. "GuService") against a real folder name (e.g. "GuConvertorService").</summary>
    private static int LevenshteinDistance(string a, string b)
    {
        var distances = new int[a.Length + 1, b.Length + 1];

        for (var i = 0; i <= a.Length; i++)
        {
            distances[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            distances[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[a.Length, b.Length];
    }
}

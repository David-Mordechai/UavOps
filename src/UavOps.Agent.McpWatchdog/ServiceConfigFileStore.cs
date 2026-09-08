using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using UavOps.Agent.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UavOps.Agent.McpWatchdog;

/// <summary>
/// Real <see cref="IServiceConfigFileStore"/> — reads/writes actual YAML files under
/// <see cref="WatchdogOptions.ServiceConfigBasePath"/>. This is the first YAML-*writing* code in
/// the repo (every prior YamlDotNet usage, <c>AgentConfigLoader</c> and the evals project, is
/// deserialize-only), so on every write it preserves the file's leading comment/blank-line header
/// byte-for-byte rather than losing it to a naive deserialize-then-reserialize round trip — only
/// the YAML list body is regenerated. Known limitation: any comments *within* the list body itself
/// (not present in the sample file, but possible) would be lost, and untouched entries' quoting
/// style may shift cosmetically, since the whole body is regenerated rather than patched
/// per-entry.
/// </summary>
public sealed partial class ServiceConfigFileStore(WatchdogOptions options) : IServiceConfigFileStore
{
    // Matches the start of a top-level (unindented) sequence item — i.e. "- description: ..." —
    // but not a nested one like the indented "  - -c arg1" under an args: list.
    [GeneratedRegex(@"^(?=- )", RegexOptions.Multiline)]
    private static partial Regex TopLevelItemStart();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ArgsYamlConverter())
        // Defensive fallback, not a substitute for modeling real fields on ServiceConfigEntry: a
        // real config file can carry a field the watchdog itself understands that this DTO
        // doesn't yet (e.g. "group" before it was added here) — without this, any single such
        // field made every operation on that file fail outright. Any field genuinely present on
        // disk should still be added to ServiceConfigEntry so it round-trips instead of silently
        // vanishing on the next write to that file.
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ArgsYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    // Keyed by resolved full file path so a read-modify-write against one configuration's file
    // can't race with a concurrent call against that *same* file; different configurations don't
    // block each other. Mirrors the ConcurrentDictionary keying style RemoteOperationBroker
    // already uses for its correlation dictionary.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> ListConfigurationsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceConfigBasePath) || !Directory.Exists(options.ServiceConfigBasePath))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var basePath = Path.GetFullPath(options.ServiceConfigBasePath);
        IReadOnlyList<string> names = Directory.GetDirectories(basePath)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(names);
    }

    public async Task<IReadOnlyList<ServiceConfigEntry>> ReadAsync(string configurationName, CancellationToken cancellationToken)
    {
        var filePath = await ResolveConfigFilePathAsync(configurationName, cancellationToken);
        var fileLock = _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var (_, body) = await ReadHeaderAndBodyAsync(filePath, cancellationToken);
            return ParseBody(body);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task WriteAsync(string configurationName, IReadOnlyList<ServiceConfigEntry> entries, CancellationToken cancellationToken)
    {
        var filePath = await ResolveConfigFilePathAsync(configurationName, cancellationToken);
        var fileLock = _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var (header, _) = await ReadHeaderAndBodyAsync(filePath, cancellationToken);
            var body = InsertBlankLinesBetweenTopLevelItems(Serializer.Serialize(entries.ToList()));
            var content = string.IsNullOrEmpty(header) ? body : header + Environment.NewLine + Environment.NewLine + body;
            await File.WriteAllTextAsync(filePath, content, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>Resolves a configuration name to its real config file path. <paramref
    /// name="configurationName"/> ultimately originates from operator chat text, so it's only ever
    /// matched (case-insensitively) against subfolder names actually discovered under
    /// <see cref="WatchdogOptions.ServiceConfigBasePath"/> — never combined into a path directly —
    /// same reasoning <c>Agents.SimulatorAgent.LocalLessonRunner.ResolveLessonPath</c> already
    /// documents for lesson filenames.</summary>
    private async Task<string> ResolveConfigFilePathAsync(string configurationName, CancellationToken cancellationToken)
    {
        var basePath = Path.GetFullPath(options.ServiceConfigBasePath);
        var configurations = await ListConfigurationsAsync(cancellationToken);
        var matched = configurations.FirstOrDefault(c => string.Equals(c, configurationName, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
        {
            throw new InvalidOperationException($"Unknown configuration '{configurationName}'.");
        }

        var folderPath = Path.GetFullPath(Path.Combine(basePath, matched));
        if (!folderPath.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Configuration '{configurationName}' resolves outside the configured base path.");
        }

        var filePath = Path.Combine(folderPath, options.ServiceConfigFileName);
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"Config file not found for configuration '{configurationName}': '{filePath}'.");
        }

        return filePath;
    }

    /// <summary>Splits the raw file into its leading comment/blank-line header (kept byte-for-byte
    /// on write, minus any trailing blank lines — exactly one blank separator line is always
    /// re-added when writing, regardless of how many originally separated header from body) and
    /// the remaining YAML document body (the service list).</summary>
    private static async Task<(string Header, string Body)> ReadHeaderAndBodyAsync(string filePath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);

        var bodyStart = 0;
        while (bodyStart < lines.Length)
        {
            var trimmed = lines[bodyStart].TrimStart();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                break;
            }

            bodyStart++;
        }

        var headerEnd = bodyStart;
        while (headerEnd > 0 && lines[headerEnd - 1].Trim().Length == 0)
        {
            headerEnd--;
        }

        var header = string.Join(Environment.NewLine, lines[..headerEnd]);
        var body = string.Join(Environment.NewLine, lines[bodyStart..]);
        return (header, body);
    }

    private static List<ServiceConfigEntry> ParseBody(string body) =>
        string.IsNullOrWhiteSpace(body) ? [] : Deserializer.Deserialize<List<ServiceConfigEntry>>(body) ?? [];

    /// <summary>YamlDotNet's block-sequence emitter writes list items back-to-back with no blank
    /// line between them — that spacing is pure formatting, not part of the parsed
    /// <see cref="ServiceConfigEntry"/> model, so it doesn't survive the regenerate-the-whole-body
    /// round trip on its own. Re-adds one blank line before every top-level entry (after the
    /// first) so multi-service files stay as readable as the hand-authored original.</summary>
    private static string InsertBlankLinesBetweenTopLevelItems(string body)
    {
        var matches = TopLevelItemStart().Matches(body);
        for (var i = matches.Count - 1; i >= 1; i--)
        {
            body = body.Insert(matches[i].Index, Environment.NewLine);
        }

        return body;
    }
}

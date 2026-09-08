using FluentAssertions;
using UavOps.Agent.McpWatchdog;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class ServiceConfigFileStoreTests : IDisposable
{
    // ServiceConfigFileStore rejoins header lines with Environment.NewLine when writing, so the
    // expected header here must use it too rather than a hardcoded "\n" (would mismatch on
    // Windows, where Environment.NewLine is "\r\n").
    private static readonly string Header =
        "# WatchDog Service config" + Environment.NewLine +
        "# Environment variables are supported in [executable] and [args]";

    private const string SampleBody =
        "- description: Service One\n" +
        "  executable: '%MoavProducts%\\Services\\ServiceOne\\ServiceOne.exe'\n" +
        "\n" +
        "- description: Service Two\n" +
        "  executable: '%MoavProducts%\\Services\\ServiceTwo\\ServiceTwo.exe'\n" +
        "  args: '-c arg1'\n";

    private readonly string _basePath = Directory.CreateTempSubdirectory("uavops-configstore-").FullName;

    public void Dispose() => Directory.Delete(_basePath, recursive: true);

    private ServiceConfigFileStore CreateSut() =>
        new(new WatchdogOptions { ServiceConfigBasePath = _basePath, ServiceConfigFileName = "config.yml" });

    private string CreateConfiguration(string name, string body)
    {
        var folder = Path.Combine(_basePath, name);
        Directory.CreateDirectory(folder);
        var filePath = Path.Combine(folder, "config.yml");
        File.WriteAllText(filePath, Header + "\n\n" + body);
        return filePath;
    }

    [Fact]
    public async Task ListConfigurationsAsync_ReturnsDiscoveredSubfolders()
    {
        CreateConfiguration("Flight", SampleBody);
        CreateConfiguration("Simulator", SampleBody);
        var sut = CreateSut();

        var result = await sut.ListConfigurationsAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(["Flight", "Simulator"]);
    }

    [Fact]
    public async Task ReadAsync_ParsesEntries_IncludingScalarAndArrayArgs()
    {
        CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();

        var entries = await sut.ReadAsync("flight", CancellationToken.None); // case-insensitive match

        entries.Should().HaveCount(2);
        entries[0].Description.Should().Be("Service One");
        entries[0].Executable.Should().Be(@"%MoavProducts%\Services\ServiceOne\ServiceOne.exe");
        entries[0].Args.Should().BeNull();
        entries[1].Description.Should().Be("Service Two");
        entries[1].Args.Should().BeEquivalentTo(["-c arg1"]); // scalar 'args' normalized into a one-element list
    }

    [Fact]
    public async Task WriteAsync_PreservesHeaderVerbatim()
    {
        var filePath = CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();
        var entries = await sut.ReadAsync("Flight", CancellationToken.None);

        await sut.WriteAsync("Flight", entries, CancellationToken.None);

        var rewritten = await File.ReadAllTextAsync(filePath);
        rewritten.Should().StartWith(Header + Environment.NewLine + Environment.NewLine);
    }

    [Fact]
    public async Task WriteAsync_SeparatesTopLevelEntriesWithABlankLine()
    {
        var filePath = CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();
        var entries = await sut.ReadAsync("Flight", CancellationToken.None);

        await sut.WriteAsync("Flight", entries, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(filePath);
        var firstItemIndex = Array.FindIndex(lines, l => l.Contains("Service Two", StringComparison.Ordinal));
        firstItemIndex.Should().BeGreaterThan(0);
        lines[firstItemIndex - 1].Should().BeEmpty("a blank line should separate top-level service entries, even though " +
            "YamlDotNet's block-sequence emitter doesn't add one on its own");
    }

    [Fact]
    public async Task WriteAsync_QuotesStringValues_ButNotKeys_MatchingTheFilesOwnDocumentedExamples()
    {
        var filePath = CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();
        var entries = await sut.ReadAsync("Flight", CancellationToken.None);

        await sut.WriteAsync("Flight", entries, CancellationToken.None);

        var rewritten = await File.ReadAllTextAsync(filePath);
        rewritten.Should().Contain("- description: 'Service One'");
        rewritten.Should().Contain("  executable: '%MoavProducts%\\Services\\ServiceOne\\ServiceOne.exe'");
        rewritten.Should().Contain("  - '-c arg1'"); // args item, single-quoted
        rewritten.Should().NotContain("'description':").And.NotContain("'executable':").And.NotContain("'args':");
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsAddedEntry()
    {
        CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();
        var entries = (await sut.ReadAsync("Flight", CancellationToken.None)).ToList();
        entries.Add(new ServiceConfigEntry { Description = "Service Three", Executable = @"%MoavProducts%\Services\ServiceThree\ServiceThree.exe" });

        await sut.WriteAsync("Flight", entries, CancellationToken.None);
        var reread = await sut.ReadAsync("Flight", CancellationToken.None);

        reread.Should().HaveCount(3);
        reread.Should().Contain(e => e.Description == "Service Three");
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsRemovedEntry()
    {
        CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();
        var entries = (await sut.ReadAsync("Flight", CancellationToken.None)).Where(e => e.Description != "Service Two").ToList();

        await sut.WriteAsync("Flight", entries, CancellationToken.None);
        var reread = await sut.ReadAsync("Flight", CancellationToken.None);

        reread.Should().ContainSingle().Which.Description.Should().Be("Service One");
    }

    [Fact]
    public async Task ReadAsync_UnknownConfiguration_Throws()
    {
        CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync("DoesNotExist", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Unknown configuration*");
    }

    [Fact]
    public async Task ReadAsync_PathTraversalAttempt_Throws()
    {
        CreateConfiguration("Flight", SampleBody);
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync(@"..\..\Windows", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReadAsync_ConfigurationFolderExistsButNoConfigFile_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_basePath, "Empty"));
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync("Empty", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Config file not found*");
    }

    [Fact]
    public async Task ReadAsync_EntryWithGroupField_ParsesIt()
    {
        // Regression: a real production config.yml had a 'group' field on an entry that
        // ServiceConfigEntry didn't model yet, which crashed every operation on that file — see
        // ReadAsync_EntryWithStillUnknownField_DoesNotCrashTheWholeFile below for the general case.
        CreateConfiguration("Flight",
            "- description: 'Test Service'\n" +
            "  executable: 'C:\\Windows\\System32\\notepad.exe'\n" +
            "  group: 'Core'\n");
        var sut = CreateSut();

        var entries = await sut.ReadAsync("Flight", CancellationToken.None);

        entries.Should().ContainSingle().Which.Group.Should().Be("Core");
    }

    [Fact]
    public async Task ReadAsync_EntryWithStillUnknownField_DoesNotCrashTheWholeFile()
    {
        // The real incident this guards against: the watchdog's own schema can carry a field
        // ServiceConfigEntry doesn't model yet (like 'group' before it was added). YamlDotNet's
        // default strict deserializer throws the instant it hits ANY unrecognized field anywhere
        // in the document, aborting the parse of the whole list — not just that one field or
        // entry — which broke every operation (list/add/update) on that configuration, including
        // entries with only already-known fields. IgnoreUnmatchedProperties() (see
        // ServiceConfigFileStore's Deserializer) is the fix under test here: a still-unknown field
        // must be skipped, not fatal, and every other known field on every entry must still parse.
        CreateConfiguration("Flight",
            "- description: 'Service One'\n" +
            "  executable: '%MoavProducts%\\Services\\ServiceOne\\ServiceOne.exe'\n" +
            "  someFutureField: 'unmodeled value'\n" +
            "\n" +
            "- description: 'Service Two'\n" +
            "  executable: '%MoavProducts%\\Services\\ServiceTwo\\ServiceTwo.exe'\n" +
            "  healthEndPoint: 'http://localhost:1111/_health'\n");
        var sut = CreateSut();

        var entries = await sut.ReadAsync("Flight", CancellationToken.None);

        entries.Should().HaveCount(2);
        entries[0].Description.Should().Be("Service One");
        entries[1].Description.Should().Be("Service Two");
        entries[1].HealthEndPoint.Should().Be("http://localhost:1111/_health");
    }
}

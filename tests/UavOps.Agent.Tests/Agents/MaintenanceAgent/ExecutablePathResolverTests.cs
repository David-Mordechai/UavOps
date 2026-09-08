using FluentAssertions;
using UavOps.Agent.McpWatchdog;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class ExecutablePathResolverTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("uavops-exepath-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private ExecutablePathResolver CreateSut(Dictionary<string, string>? placeholders = null) =>
        new(new WatchdogOptions { ExecutablePlaceholders = placeholders ?? new Dictionary<string, string> { ["%Root%"] = _tempDir } });

    [Fact]
    public void Resolve_KnownPlaceholder_FileExists_ReturnsVerified()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Foo.exe"), "");
        var sut = CreateSut();

        var result = sut.Resolve(@"%Root%\Foo.exe");

        result.Kind.Should().Be(ExecutablePathResolutionKind.Verified);
        result.ExpandedPath.Should().Be(Path.Combine(_tempDir, "Foo.exe"));
    }

    [Fact]
    public void Resolve_KnownPlaceholder_FileMissing_ReturnsNotFound()
    {
        var sut = CreateSut();

        var result = sut.Resolve(@"%Root%\DoesNotExist.exe");

        result.Kind.Should().Be(ExecutablePathResolutionKind.NotFound);
    }

    [Fact]
    public void Resolve_UnrecognizedPlaceholder_ReturnsUnverifiable()
    {
        var sut = CreateSut();

        var result = sut.Resolve(@"%Unknown%\Foo.exe");

        result.Kind.Should().Be(ExecutablePathResolutionKind.Unverifiable);
        result.UnresolvedPlaceholder.Should().Be("%Unknown%");
    }

    [Fact]
    public void Resolve_LiteralPathNoPlaceholder_FileExists_ReturnsVerified()
    {
        var filePath = Path.Combine(_tempDir, "Literal.exe");
        File.WriteAllText(filePath, "");
        var sut = CreateSut();

        var result = sut.Resolve(filePath);

        result.Kind.Should().Be(ExecutablePathResolutionKind.Verified);
    }

    [Fact]
    public void Resolve_LiteralPathNoPlaceholder_FileMissing_ReturnsNotFound()
    {
        var sut = CreateSut();

        var result = sut.Resolve(Path.Combine(_tempDir, "Missing.exe"));

        result.Kind.Should().Be(ExecutablePathResolutionKind.NotFound);
    }
}

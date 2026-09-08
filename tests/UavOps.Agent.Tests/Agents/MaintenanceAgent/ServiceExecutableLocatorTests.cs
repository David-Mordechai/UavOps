using FluentAssertions;
using UavOps.Agent.McpWatchdog;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class ServiceExecutableLocatorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("uavops-svcloc-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private ServiceExecutableLocator CreateSut(params string[] serviceFolderNames)
    {
        var servicesRoot = Path.Combine(_tempDir, "Services");
        Directory.CreateDirectory(servicesRoot);
        foreach (var name in serviceFolderNames)
        {
            Directory.CreateDirectory(Path.Combine(servicesRoot, name));
        }

        return new ServiceExecutableLocator(new WatchdogOptions
        {
            ExecutablePlaceholders = new Dictionary<string, string> { ["%MoavProducts%"] = _tempDir }
        });
    }

    [Fact]
    public void Locate_ExactMatch_ResolvesImmediately()
    {
        var sut = CreateSut("GuConvertorService", "OtherService");

        var result = sut.Locate("GuConvertorService");

        result.Kind.Should().Be(ServiceExecutableLookupKind.Resolved);
        result.ExecutablePath.Should().Be(@"%MoavProducts%\Services\GuConvertorService\GuConvertorService.exe");
    }

    [Fact]
    public void Locate_SubstringLikeHint_UniqueMatch_ResolvesWithoutAsking()
    {
        var sut = CreateSut("GuConvertorService", "PayloadHandlerService");

        var result = sut.Locate("GuConvertor");

        result.Kind.Should().Be(ServiceExecutableLookupKind.Resolved);
        result.ExecutablePath.Should().Be(@"%MoavProducts%\Services\GuConvertorService\GuConvertorService.exe");
    }

    [Fact]
    public void Locate_NonContiguousHint_UniqueClosestMatch_ResolvesViaEditDistance()
    {
        var sut = CreateSut("GuConvertorService", "PayloadHandlerService", "TelemetryRelayService");

        var result = sut.Locate("GuService");

        result.Kind.Should().Be(ServiceExecutableLookupKind.Resolved);
        result.ExecutablePath.Should().Be(@"%MoavProducts%\Services\GuConvertorService\GuConvertorService.exe");
    }

    [Fact]
    public void Locate_MultipleEquallyCloseFolders_ReturnsAmbiguousWithCandidates()
    {
        // Both are the hint plus an equal-length suffix, so they're exactly tied on edit distance.
        var sut = CreateSut("GuConvertorServiceA", "GuConvertorServiceB");

        var result = sut.Locate("GuConvertor");

        result.Kind.Should().Be(ServiceExecutableLookupKind.Ambiguous);
        result.Candidates.Should().BeEquivalentTo(["GuConvertorServiceA", "GuConvertorServiceB"]);
    }

    [Fact]
    public void Locate_NoPlausibleMatch_ReturnsNotFound()
    {
        var sut = CreateSut("PayloadHandlerService");

        var result = sut.Locate("Zzzzzzzzzz");

        result.Kind.Should().Be(ServiceExecutableLookupKind.NotFound);
    }

    [Fact]
    public void Locate_NoFoldersAtAll_ReturnsNotFound()
    {
        var sut = CreateSut();

        var result = sut.Locate("Anything");

        result.Kind.Should().Be(ServiceExecutableLookupKind.NotFound);
    }

    [Fact]
    public void Locate_ServicesSubfolderMissing_ReturnsNotFound()
    {
        var sut = new ServiceExecutableLocator(new WatchdogOptions
        {
            ExecutablePlaceholders = new Dictionary<string, string> { ["%MoavProducts%"] = _tempDir }
        });

        var result = sut.Locate("Anything");

        result.Kind.Should().Be(ServiceExecutableLookupKind.NotFound);
    }

    [Fact]
    public void Locate_MoavProductsPlaceholderNotConfigured_ReturnsNotFound()
    {
        var sut = new ServiceExecutableLocator(new WatchdogOptions { ExecutablePlaceholders = new Dictionary<string, string>() });

        var result = sut.Locate("Anything");

        result.Kind.Should().Be(ServiceExecutableLookupKind.NotFound);
    }
}

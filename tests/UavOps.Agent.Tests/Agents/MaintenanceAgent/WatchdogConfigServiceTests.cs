using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.McpWatchdog;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class WatchdogConfigServiceTests
{
    private static WatchdogConfigService CreateSut(
        IServiceConfigFileStore? fileStore = null,
        IExecutablePathResolver? pathResolver = null,
        IServiceExecutableLocator? executableLocator = null)
    {
        var resolver = pathResolver;
        if (resolver is null)
        {
            // Verified by default so tests not specifically about resolution don't need to stub it
            // — only applied when the caller didn't supply its own pre-configured resolver.
            resolver = Substitute.For<IExecutablePathResolver>();
            resolver.Resolve(Arg.Any<string>()).Returns(callInfo =>
                new ExecutablePathResolution(ExecutablePathResolutionKind.Verified, callInfo.Arg<string>()!));
        }

        var locator = executableLocator;
        if (locator is null)
        {
            // NotFound by default, so tests not specifically about disk-based lookup fall through
            // to (and exercise) the same sibling-naming-convention path they did before this
            // locator existed.
            locator = Substitute.For<IServiceExecutableLocator>();
            locator.Locate(Arg.Any<string>()).Returns(new ServiceExecutableLookupResult(ServiceExecutableLookupKind.NotFound));
        }

        return new WatchdogConfigService(
            fileStore ?? Substitute.For<IServiceConfigFileStore>(),
            resolver,
            locator,
            NullLogger<WatchdogConfigService>.Instance);
    }

    private static IServiceConfigFileStore CreateFileStore(params ServiceConfigEntry[] entries)
    {
        var store = Substitute.For<IServiceConfigFileStore>();
        store.ReadAsync("Flight", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ServiceConfigEntry>)entries.ToList());
        return store;
    }

    [Fact]
    public async Task AddConfiguredService_DuplicateDescription_ReturnsInvalid_NeverWrites()
    {
        var store = CreateFileStore(new ServiceConfigEntry { Description = "Service One", Executable = "x.exe" });
        var sut = CreateSut(fileStore: store);

        var result = await sut.AddConfiguredService("Flight", "Service One", "y.exe", null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
        await store.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Fact]
    public async Task AddConfiguredService_ExplicitExecutable_NotFound_ReturnsInvalid_NeverWrites()
    {
        var store = CreateFileStore();
        var resolver = Substitute.For<IExecutablePathResolver>();
        resolver.Resolve("missing.exe").Returns(new ExecutablePathResolution(ExecutablePathResolutionKind.NotFound, @"C:\missing.exe"));
        var sut = CreateSut(store, resolver);

        var result = await sut.AddConfiguredService("Flight", "New Svc", "missing.exe", null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
        await store.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Fact]
    public async Task AddConfiguredService_ExplicitExecutable_Verified_Succeeds_AndWrites()
    {
        var store = CreateFileStore();
        var sut = CreateSut(fileStore: store);

        var result = await sut.AddConfiguredService("Flight", "New Svc", "real.exe", null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list => list!.Any(e => e.Description == "New Svc" && e.Executable == "real.exe")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddConfiguredService_NoExecutable_ConsistentSiblingPattern_InfersPath()
    {
        var store = CreateFileStore(
            new ServiceConfigEntry { Description = "Service One", Executable = @"%MoavProducts%\Services\ServiceOne\ServiceOne.exe" },
            new ServiceConfigEntry { Description = "Service Two", Executable = @"%MoavProducts%\Services\ServiceTwo\ServiceTwo.exe" });
        var sut = CreateSut(fileStore: store);

        var result = await sut.AddConfiguredService("Flight", "Service Three", null, null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list =>
                list!.Any(e => e.Description == "Service Three" && e.Executable == @"%MoavProducts%\Services\ServiceThree\ServiceThree.exe")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddConfiguredService_NoExecutable_NoSiblings_ReturnsInvalid_AsksForExplicitPath()
    {
        var store = CreateFileStore();
        var sut = CreateSut(fileStore: store);

        var result = await sut.AddConfiguredService("Flight", "New Svc", null, null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("please provide the executable path explicitly");
    }

    [Fact]
    public async Task AddConfiguredService_NoExecutable_LocatorResolves_UsesLocatedPath()
    {
        var store = CreateFileStore();
        var locator = Substitute.For<IServiceExecutableLocator>();
        locator.Locate("Gu Convertor").Returns(new ServiceExecutableLookupResult(
            ServiceExecutableLookupKind.Resolved, @"%MoavProducts%\Services\GuConvertorService\GuConvertorService.exe"));
        var sut = CreateSut(fileStore: store, executableLocator: locator);

        var result = await sut.AddConfiguredService("Flight", "Gu Convertor", null, null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list =>
                list!.Any(e => e.Description == "Gu Convertor" && e.Executable == @"%MoavProducts%\Services\GuConvertorService\GuConvertorService.exe")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddConfiguredService_NoExecutable_LocatorAmbiguous_ReturnsInvalidWithCandidates_NeverWrites()
    {
        var store = CreateFileStore();
        var locator = Substitute.For<IServiceExecutableLocator>();
        locator.Locate("Gu Convertor").Returns(new ServiceExecutableLookupResult(
            ServiceExecutableLookupKind.Ambiguous, Candidates: ["GuConvertorServiceA", "GuConvertorServiceB"]));
        var sut = CreateSut(fileStore: store, executableLocator: locator);

        var result = await sut.AddConfiguredService("Flight", "Gu Convertor", null, null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GuConvertorServiceA").And.Contain("GuConvertorServiceB");
        await store.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Fact]
    public async Task AddConfiguredService_UnrecognizedPlaceholder_SucceedsWithNote()
    {
        var store = CreateFileStore();
        var resolver = Substitute.For<IExecutablePathResolver>();
        resolver.Resolve("%Unknown%\\x.exe")
            .Returns(new ExecutablePathResolution(ExecutablePathResolutionKind.Unverifiable, "%Unknown%\\x.exe", "%Unknown%"));
        var sut = CreateSut(store, resolver);

        var result = await sut.AddConfiguredService("Flight", "New Svc", "%Unknown%\\x.exe", null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight", Arg.Any<IReadOnlyList<ServiceConfigEntry>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateConfiguredService_NotFound_ReturnsInvalid()
    {
        var store = CreateFileStore();
        var sut = CreateSut(fileStore: store);

        var result = await sut.UpdateConfiguredService("Flight", "Does Not Exist", null, null, null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No service named");
    }

    [Fact]
    public async Task UpdateConfiguredService_OnlySuppliedFieldsChange()
    {
        var store = CreateFileStore(new ServiceConfigEntry
        {
            Description = "Service One",
            Executable = "original.exe",
            Retries = 3
        });
        var sut = CreateSut(fileStore: store);

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", newDescription: null, executable: null,
            args: ["--foo"], id: null, disabled: null, retries: null, isManaged: null, healthEndPoint: null, group: null,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list =>
                list!.Single().Executable == "original.exe" && // unchanged
                list!.Single().Retries == 3 &&                 // unchanged
                list!.Single().Args!.SequenceEqual(new List<string> { "--foo" })), // changed
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateConfiguredService_EnablingAService_ClearsAPreviouslySetDisabledTrue()
    {
        // "Enable" maps to disabled: false — the only on/off toggle exposed to chat.
        var store = CreateFileStore(new ServiceConfigEntry { Description = "Service One", Executable = "a.exe", Disabled = true });
        var sut = CreateSut(fileStore: store);

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", newDescription: null, executable: null,
            args: null, id: null, disabled: false, retries: null, isManaged: null, healthEndPoint: null, group: null,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list => list!.Single().Disabled == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateConfiguredService_DisablingAService_ClearsAStaleLegacyEnabledField()
    {
        // A pre-existing hand-authored "enabled: true" must not linger and contradict a
        // chat-driven "disabled: true" — touching `disabled` always clears the legacy `enabled`.
        var store = CreateFileStore(new ServiceConfigEntry { Description = "Service One", Executable = "a.exe", Enabled = true });
        var sut = CreateSut(fileStore: store);

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", newDescription: null, executable: null,
            args: null, id: null, disabled: true, retries: null, isManaged: null, healthEndPoint: null, group: null,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list => list!.Single().Disabled == true && list!.Single().Enabled == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateConfiguredService_RenameCollidesWithAnotherEntry_ReturnsInvalid()
    {
        var store = CreateFileStore(
            new ServiceConfigEntry { Description = "Service One", Executable = "a.exe" },
            new ServiceConfigEntry { Description = "Service Two", Executable = "b.exe" });
        var sut = CreateSut(fileStore: store);

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", newDescription: "Service Two", executable: null,
            args: null, id: null, disabled: null, retries: null, isManaged: null, healthEndPoint: null, group: null,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task UpdateConfiguredService_NewExecutable_NotFound_ReturnsInvalid_NeverWrites()
    {
        var store = CreateFileStore(new ServiceConfigEntry { Description = "Service One", Executable = "a.exe" });
        var resolver = Substitute.For<IExecutablePathResolver>();
        resolver.Resolve("missing.exe").Returns(new ExecutablePathResolution(ExecutablePathResolutionKind.NotFound, @"C:\missing.exe"));
        var sut = CreateSut(store, resolver);

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", newDescription: null, executable: "missing.exe",
            args: null, id: null, disabled: null, retries: null, isManaged: null, healthEndPoint: null, group: null,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        await store.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Fact]
    public async Task RemoveConfiguredService_NotFound_ReturnsInvalid()
    {
        var store = CreateFileStore();
        var sut = CreateSut(fileStore: store);

        var result = await sut.RemoveConfiguredService("Flight", "Does Not Exist", CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveConfiguredService_Found_RemovesAndWrites()
    {
        var store = CreateFileStore(
            new ServiceConfigEntry { Description = "Service One", Executable = "a.exe" },
            new ServiceConfigEntry { Description = "Service Two", Executable = "b.exe" });
        var sut = CreateSut(fileStore: store);

        var result = await sut.RemoveConfiguredService("Flight", "Service One", CancellationToken.None);

        result.Success.Should().BeTrue();
        await store.Received(1).WriteAsync("Flight",
            Arg.Is<IReadOnlyList<ServiceConfigEntry>>(list => list!.Count == 1 && list.Single().Description == "Service Two"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnyMethod_UnderlyingException_ReturnsErrorInsteadOfThrowing()
    {
        var store = Substitute.For<IServiceConfigFileStore>();
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<ServiceConfigEntry>>(new InvalidOperationException("boom")));
        var sut = CreateSut(fileStore: store);

        var result = await sut.ListConfiguredServices("Flight", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("boom");
    }
}

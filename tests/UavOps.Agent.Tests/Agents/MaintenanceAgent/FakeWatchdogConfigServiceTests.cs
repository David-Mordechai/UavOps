using FluentAssertions;
using UavOps.Agent.Watchdog.Fake;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class FakeWatchdogConfigServiceTests
{
    [Fact]
    public async Task ListConfigurations_ReturnsSeededNames()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.ListConfigurations(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new[] { "Flight", "Simulator" });
    }

    [Fact]
    public async Task ListConfiguredServices_UnknownConfiguration_ReturnsInvalid()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.ListConfiguredServices("DoesNotExist", CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task AddConfiguredService_NoRealFilesystemCheck_SucceedsEvenForAPathThatCannotExist()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.AddConfiguredService(
            "Flight", "Nonexistent Path Service", @"%NoSuchPlaceholder%\definitely\not\real.exe",
            null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task AddConfiguredService_DuplicateDescription_ReturnsInvalid()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.AddConfiguredService(
            "Flight", "Service One", "whatever.exe", null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task AddConfiguredService_NoExecutable_InfersFromSiblingPattern()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.AddConfiguredService(
            "Flight", "Service Three", null, null, null, null, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();

        var listed = await sut.ListConfiguredServices("Flight", CancellationToken.None);
        listed.Value.Should().BeAssignableTo<IEnumerable<UavOps.Agent.Contracts.ServiceConfigEntry>>()
            .Subject.Should().Contain(e => e.Description == "Service Three"
                && e.Executable == @"%MoavProducts%\Services\ServiceThree\ServiceThree.exe");
    }

    [Fact]
    public async Task UpdateConfiguredService_OnlySuppliedFieldsChange()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", newDescription: null, executable: null,
            args: ["--verbose"], id: null, disabled: null, retries: null, isManaged: null, healthEndPoint: null, group: null,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        var listed = await sut.ListConfiguredServices("Flight", CancellationToken.None);
        var updated = listed.Value.Should().BeAssignableTo<IEnumerable<UavOps.Agent.Contracts.ServiceConfigEntry>>()
            .Subject.Single(e => e.Description == "Service One");
        updated.Args.Should().BeEquivalentTo(["--verbose"]);
        updated.Executable.Should().Be(@"%MoavProducts%\Services\ServiceOne\ServiceOne.exe"); // unchanged
    }

    [Fact]
    public async Task UpdateConfiguredService_DisablingThenEnabling_EndsUpNotDisabled()
    {
        var sut = new FakeWatchdogConfigService();
        await sut.UpdateConfiguredService(
            "Flight", "Service One", null, null, null, null, disabled: true, null, null, null, null, CancellationToken.None);

        var result = await sut.UpdateConfiguredService(
            "Flight", "Service One", null, null, null, null, disabled: false, null, null, null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        var listed = await sut.ListConfiguredServices("Flight", CancellationToken.None);
        var updated = listed.Value.Should().BeAssignableTo<IEnumerable<UavOps.Agent.Contracts.ServiceConfigEntry>>()
            .Subject.Single(e => e.Description == "Service One");
        updated.Disabled.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveConfiguredService_RemovesEntry()
    {
        var sut = new FakeWatchdogConfigService();

        var result = await sut.RemoveConfiguredService("Flight", "Service One", CancellationToken.None);

        result.Success.Should().BeTrue();
        var listed = await sut.ListConfiguredServices("Flight", CancellationToken.None);
        listed.Value.Should().BeAssignableTo<IEnumerable<UavOps.Agent.Contracts.ServiceConfigEntry>>()
            .Subject.Should().NotContain(e => e.Description == "Service One");
    }
}

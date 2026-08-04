using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests.Agents.SimulatorAgent;

public class SimulatorServiceTests
{
    /// <summary>A ready-to-go IVmwareController double: WaitForHostReadyAsync/
    /// WaitForVmToolsRunningAsync both default to true (NSubstitute otherwise defaults Task&lt;bool&gt;
    /// to false, which would fail every Ensure* test unless it's explicitly about the timeout
    /// path itself).</summary>
    private static IVmwareController CreateReadyVmwareController()
    {
        var vmware = Substitute.For<IVmwareController>();
        vmware.WaitForHostReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        vmware.WaitForVmToolsRunningAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        return vmware;
    }

    private static SimulatorService CreateSut(
        IVmwareController? vmwareController = null, ILocalLessonRunner? lessonRunner = null,
        ISimulatorLessonJobQueue? lessonJobQueue = null, SimulatorOptions? options = null)
    {
        return new SimulatorService(
            vmwareController ?? CreateReadyVmwareController(),
            lessonRunner ?? Substitute.For<ILocalLessonRunner>(),
            lessonJobQueue ?? Substitute.For<ISimulatorLessonJobQueue>(),
            options ?? new SimulatorOptions { LessonsFolder = @"C:\lessons" },
            NullLogger<SimulatorService>.Instance);
    }

    [Fact]
    public async Task EnsureVmwareHostRunning_AlreadyRunning_DoesNotStartIt()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsHostRunning().Returns(true);
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureVmwareHostRunning(CancellationToken.None);

        result.Success.Should().BeTrue();
        vmware.DidNotReceive().StartHost();
    }

    [Fact]
    public async Task EnsureVmwareHostRunning_NotRunning_StartsIt()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsHostRunning().Returns(false);
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureVmwareHostRunning(CancellationToken.None);

        result.Success.Should().BeTrue();
        vmware.Received(1).StartHost();
    }

    [Fact]
    public async Task EnsureVmwareHostRunning_NeverBecomesReady_TimesOutAndFails()
    {
        var vmware = Substitute.For<IVmwareController>();
        vmware.IsHostRunning().Returns(true);
        vmware.WaitForHostReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureVmwareHostRunning(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("did not become ready");
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_AlreadyRunning_DoesNotStartIt()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        result.Success.Should().BeTrue();
        await vmware.DidNotReceive().StartVmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_NotRunning_StartsIt()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        result.Success.Should().BeTrue();
        await vmware.Received(1).StartVmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_GuestNeverBecomesReady_TimesOutAndFails()
    {
        var vmware = Substitute.For<IVmwareController>();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        vmware.WaitForVmToolsRunningAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("did not finish booting");
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_ReconnectsAdaptersBeforeWaitingForGuestReady()
    {
        // Sequencing matters: a flaky management NIC must be reconnected before we wait on any
        // guest-readiness signal, or a network-dependent readiness check could hang/timeout for
        // a guest that's actually already done booting.
        var callOrder = new List<string>();
        var vmware = Substitute.For<IVmwareController>();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        vmware.When(v => v.ConnectNetworkAdapterAsync("ethernet0", Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("connect"));
        vmware.WaitForVmToolsRunningAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("wait-tools"); return true; });
        var options = new SimulatorOptions { NetworkAdapterDeviceNames = ["ethernet0"] };
        var sut = CreateSut(vmwareController: vmware, options: options);

        await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        callOrder.Should().Equal("connect", "wait-tools");
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_ReconnectsEveryConfiguredNetworkAdapter()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        var options = new SimulatorOptions { NetworkAdapterDeviceNames = ["ethernet0", "ethernet1", "ethernet2"] };
        var sut = CreateSut(vmwareController: vmware, options: options);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        result.Success.Should().BeTrue();
        await vmware.Received(1).ConnectNetworkAdapterAsync("ethernet0", Arg.Any<CancellationToken>());
        await vmware.Received(1).ConnectNetworkAdapterAsync("ethernet1", Arg.Any<CancellationToken>());
        await vmware.Received(1).ConnectNetworkAdapterAsync("ethernet2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_OneAdapterFailsAllAttempts_StillSucceedsOverall_ButReportsIt()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        vmware.ConnectNetworkAdapterAsync("ethernet1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("vmrun failed")));
        var options = new SimulatorOptions
        {
            NetworkAdapterDeviceNames = ["ethernet0", "ethernet1"],
            NetworkAdapterReconnectAttempts = 2,
            NetworkAdapterReconnectRetryDelaySeconds = 0
        };
        var sut = CreateSut(vmwareController: vmware, options: options);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        // One adapter failing to reconnect shouldn't fail "is the VM ready" overall — it's
        // reported, not fatal (mirrors clicking Connect on a device that just won't cooperate).
        result.Success.Should().BeTrue();
        await vmware.Received(1).ConnectNetworkAdapterAsync("ethernet0", Arg.Any<CancellationToken>());
        await vmware.Received(2).ConnectNetworkAdapterAsync("ethernet1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureSimulatorVmRunning_AdapterFailsThenSucceeds_RetriesAndReconnects()
    {
        var vmware = CreateReadyVmwareController();
        vmware.IsVmRunningAsync(Arg.Any<CancellationToken>()).Returns(true);
        var callCount = 0;
        vmware.ConnectNetworkAdapterAsync("ethernet0", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount < 3 ? Task.FromException(new InvalidOperationException("not ready yet")) : Task.CompletedTask;
            });
        var options = new SimulatorOptions
        {
            NetworkAdapterDeviceNames = ["ethernet0"],
            NetworkAdapterReconnectAttempts = 3,
            NetworkAdapterReconnectRetryDelaySeconds = 0
        };
        var sut = CreateSut(vmwareController: vmware, options: options);

        var result = await sut.EnsureSimulatorVmRunning(CancellationToken.None);

        // Succeeds on the 3rd attempt — a real timing-race scenario, distinguishable in the logs
        // from a permanent failure by the fact it eventually stopped failing.
        result.Success.Should().BeTrue();
        callCount.Should().Be(3);
        await vmware.Received(3).ConnectNetworkAdapterAsync("ethernet0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListSimulatorLessons_ReturnsWhatTheRunnerLists()
    {
        var runner = Substitute.For<ILocalLessonRunner>();
        runner.ListLessons().Returns(new List<string> { "lesson1.ps1", "lesson2.ps1" });
        var sut = CreateSut(lessonRunner: runner);

        var result = await sut.ListSimulatorLessons(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new List<string> { "lesson1.ps1", "lesson2.ps1" });
    }

    [Fact]
    public async Task RunSimulatorLesson_EnqueuesJob_AndReturnsQueuedImmediately()
    {
        var queue = Substitute.For<ISimulatorLessonJobQueue>();
        var sut = CreateSut(lessonJobQueue: queue);

        var result = await sut.RunSimulatorLesson("lesson1.ps1", CancellationToken.None);

        result.Success.Should().BeTrue();
        queue.Received(1).Enqueue(Arg.Is<SimulatorLessonJob>(j => j!.LessonName == "lesson1.ps1"));
    }

    [Fact]
    public async Task AnyMethod_UnderlyingException_ReturnsErrorInsteadOfThrowing()
    {
        var vmware = Substitute.For<IVmwareController>();
        vmware.When(v => v.IsHostRunning()).Do(_ => throw new InvalidOperationException("boom"));
        var sut = CreateSut(vmwareController: vmware);

        var result = await sut.EnsureVmwareHostRunning(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("boom");
    }
}

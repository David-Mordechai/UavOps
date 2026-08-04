using FluentAssertions;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Tooling;

public class OperationCatalogTests
{
    private static OperationCatalog CreateSut() => new(typeof(IOperationService));

    [Fact]
    public void Constructor_ExtractsAllTwelveOperations()
    {
        var catalog = CreateSut();

        catalog.Operations.Should().HaveCount(12);
        catalog.Operations.Keys.Should().Contain(["ListFleet", "Navigate", "SetSpeed", "SetTrackingMode"]);
    }

    [Fact]
    public void TryResolve_KnownOperation_ExtractsParametersExcludingCancellationToken()
    {
        var catalog = CreateSut();

        catalog.TryResolve("SetSpeed", out var descriptor).Should().BeTrue();
        descriptor!.Parameters.Select(p => p.Name).Should().BeEquivalentTo(["tailNumber", "speedKts"]);
        descriptor.Parameters.Single(p => p.Name == "tailNumber").ClrType.Should().Be(typeof(string));
        descriptor.Parameters.Single(p => p.Name == "speedKts").ClrType.Should().Be(typeof(int));
    }

    [Fact]
    public void TryResolve_UnknownOperation_ReturnsFalse()
    {
        var catalog = CreateSut();

        catalog.TryResolve("DoesNotExist", out var descriptor).Should().BeFalse();
        descriptor.Should().BeNull();
    }

    [Fact]
    public void TryResolve_UploadWaypoints_ExposesListParameterType()
    {
        var catalog = CreateSut();

        catalog.TryResolve("UploadWaypoints", out var descriptor).Should().BeTrue();
        var waypointsParam = descriptor!.Parameters.Single(p => p.Name == "waypoints");
        waypointsParam.ClrType.Should().Be(typeof(List<Waypoint>));
    }
}

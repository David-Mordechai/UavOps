using FluentAssertions;
using Microsoft.OpenApi.Models;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class OpenApiToolCatalogTests
{
    [Fact]
    public void Constructor_ExtractsPathAndBodyParameters()
    {
        var catalog = new OpenApiToolCatalog(TestFixtures.SetSpeedDocument());

        catalog.TryResolve("SetSpeed", out var descriptor).Should().BeTrue();
        descriptor!.HttpMethod.Should().Be(OperationType.Post);
        descriptor.Path.Should().Be("/uavs/{tailNumber}/speed");

        var tailNumberParam = descriptor.Parameters.Single(p => p.Name == "tailNumber");
        tailNumberParam.Location.Should().Be(ParamLocation.Path);
        tailNumberParam.Required.Should().BeTrue();

        var speedParam = descriptor.Parameters.Single(p => p.Name == "speedKts");
        speedParam.Location.Should().Be(ParamLocation.BodyProperty);
        speedParam.Required.Should().BeTrue();
    }

    [Fact]
    public void TryResolve_UnknownOperationId_ReturnsFalse()
    {
        var catalog = new OpenApiToolCatalog(new OpenApiDocument { Paths = new OpenApiPaths() });

        catalog.TryResolve("DoesNotExist", out var descriptor).Should().BeFalse();
        descriptor.Should().BeNull();
    }

    [Fact]
    public void Constructor_SkipsOperationsWithoutOperationId()
    {
        var document = new OpenApiDocument
        {
            Paths = new OpenApiPaths
            {
                ["/nameless"] = new OpenApiPathItem
                {
                    Operations = new Dictionary<OperationType, OpenApiOperation>
                    {
                        [OperationType.Get] = new OpenApiOperation()
                    }
                }
            }
        };

        var catalog = new OpenApiToolCatalog(document);

        catalog.Operations.Should().BeEmpty();
    }
}

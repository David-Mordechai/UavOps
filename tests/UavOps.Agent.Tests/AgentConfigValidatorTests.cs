using FluentAssertions;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class AgentConfigValidatorTests
{
    [Fact]
    public void Validate_UnknownOperationId_Throws()
    {
        var catalog = new OpenApiToolCatalog(TestFixtures.SetSpeedDocument());
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools = [new AgentToolConfig { OperationId = "DoesNotExist", Description = "x" }]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, catalog);

        act.Should().Throw<InvalidOperationException>().WithMessage("*DoesNotExist*");
    }

    [Fact]
    public void Validate_MissingRequiredParameterDescription_Throws()
    {
        var catalog = new OpenApiToolCatalog(TestFixtures.SetSpeedDocument());
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools = [new AgentToolConfig { OperationId = "SetSpeed", Description = "x" }] // no Parameters described at all
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, catalog);

        act.Should().Throw<InvalidOperationException>().WithMessage("*speedKts*");
    }

    [Fact]
    public void Validate_FixedParameterCoversRequiredParameter_DoesNotThrow()
    {
        var catalog = new OpenApiToolCatalog(TestFixtures.SetSpeedDocument());
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools =
                [
                    new AgentToolConfig
                    {
                        OperationId = "SetSpeed",
                        Description = "x",
                        Parameters = new Dictionary<string, string> { ["speedKts"] = "the speed" },
                        FixedParameters = new Dictionary<string, string> { ["tailNumber"] = "UAV-1" }
                    }
                ]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, catalog);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownDelegate_Throws()
    {
        var catalog = new OpenApiToolCatalog(TestFixtures.SetSpeedDocument());
        var agents = new Dictionary<string, AgentConfig>
        {
            ["MainAgent"] = new AgentConfig { Instructions = "x", Delegates = ["GhostAgent"] }
        };

        var act = () => AgentConfigValidator.Validate(agents, catalog);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GhostAgent*");
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var catalog = new OpenApiToolCatalog(TestFixtures.SetSpeedDocument());
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools =
                [
                    new AgentToolConfig
                    {
                        OperationId = "SetSpeed",
                        Description = "x",
                        Parameters = new Dictionary<string, string>
                        {
                            ["tailNumber"] = "the tail number",
                            ["speedKts"] = "the speed"
                        }
                    }
                ]
            },
            ["MainAgent"] = new AgentConfig { Instructions = "x", Delegates = ["FlightControlAgent"] }
        };

        var act = () => AgentConfigValidator.Validate(agents, catalog);

        act.Should().NotThrow();
    }
}

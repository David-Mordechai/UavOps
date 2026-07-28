using FluentAssertions;
using UavOps.Agent.Operations;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class AgentConfigValidatorTests
{
    private static OperationCatalog Catalog() => new(typeof(IOperationService));

    [Fact]
    public void Validate_UnknownOperation_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools = [new AgentToolConfig { Operation = "DoesNotExist", Description = "x" }]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*DoesNotExist*");
    }

    [Fact]
    public void Validate_MissingRequiredParameterDescription_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "x" }] // no Parameters described at all
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*speedKts*");
    }

    [Fact]
    public void Validate_FixedParameterCoversRequiredParameter_DoesNotThrow()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools =
                [
                    new AgentToolConfig
                    {
                        Operation = "SetSpeed",
                        Description = "x",
                        Parameters = new Dictionary<string, string> { ["speedKts"] = "the speed" },
                        FixedParameters = new Dictionary<string, string> { ["tailNumber"] = "UAV-1" }
                    }
                ]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownDelegate_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["MainAgent"] = new AgentConfig { Instructions = "x", Delegates = ["GhostAgent"] }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*GhostAgent*");
    }

    [Fact]
    public void Validate_MissingInstructions_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig { Instructions = "" }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*FlightControlAgent*Instructions*");
    }

    [Fact]
    public void Validate_ToolMissingDescription_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "" }]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*SetSpeed*Description*");
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Tools =
                [
                    new AgentToolConfig
                    {
                        Operation = "SetSpeed",
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

        var act = () => AgentConfigValidator.Validate(agents, Catalog());

        act.Should().NotThrow();
    }
}

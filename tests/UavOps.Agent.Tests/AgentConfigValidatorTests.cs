using FluentAssertions;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class AgentConfigValidatorTests
{
    private static OperationCatalog Catalog() => new(typeof(IOperationService));
    private static OperationCatalog SimulatorInfraCatalog() => new(typeof(ISimulatorService));
    private static OperationCatalog WatchdogCatalog() => new(typeof(IWatchdogService));
    private static OperationCatalog WatchdogConfigCatalog() => new(typeof(IWatchdogConfigService));

    [Fact]
    public void Validate_UnknownOperation_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "x",
                Tools = [new AgentToolConfig { Operation = "DoesNotExist", Description = "x" }]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

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
                Description = "x",
                Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "x" }] // no Parameters described at all
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

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
                Description = "x",
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

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MissingInstructions_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig { Instructions = "", Description = "x" }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*FlightControlAgent*Instructions*");
    }

    [Fact]
    public void Validate_MissingDescriptionOnNonRootAgent_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig { Instructions = "x", Description = "" }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*FlightControlAgent*Description*");
    }

    [Fact]
    public void Validate_ToolMissingDescription_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "x",
                Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "" }]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*SetSpeed*Description*");
    }

    [Fact]
    public void Validate_UnknownChild_Throws()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "x", Children = ["DoesNotExistAgent"] }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().Throw<InvalidOperationException>().WithMessage("*BrainAgent*DoesNotExistAgent*");
    }

    [Fact]
    public void Validate_KnownChild_DoesNotThrow()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["BrainAgent"] = new AgentConfig { Instructions = "x", Children = ["MoavAgent"] },
            ["MoavAgent"] = new AgentConfig { Instructions = "x", Description = "Handles live ops.", Children = [] }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_OperatorPromptTool_SkipsCatalogResolution()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["SimulatorInfrastructureAgent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "x",
                Tools =
                [
                    new AgentToolConfig
                    {
                        Kind = "OperatorPrompt",
                        Operation = "AskOperatorWhichLesson",
                        Description = "Ask which lesson to run."
                        // Deliberately no Parameters entry and not a real catalog operation —
                        // an OperatorPrompt tool isn't resolved against any catalog, so neither
                        // should trip validation.
                    }
                ]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_SimulatorInfraOperation_Resolves()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["SimulatorInfrastructureAgent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "x",
                Tools = [new AgentToolConfig { Operation = "ListSimulatorLessons", Description = "x" }]
            }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var agents = new Dictionary<string, AgentConfig>
        {
            ["FlightControlAgent"] = new AgentConfig
            {
                Instructions = "x",
                Description = "Handles flight controls.",
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
            ["BrainAgent"] = new AgentConfig { Instructions = "x" }
        };

        var act = () => AgentConfigValidator.Validate(agents, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog());

        act.Should().NotThrow();
    }
}

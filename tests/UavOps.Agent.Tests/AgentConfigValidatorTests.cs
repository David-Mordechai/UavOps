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
    private static OpenAiOptions OpenAi() => new();

    [Fact]
    public void Validate_UnknownOperation_Throws()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools = [new AgentToolConfig { Operation = "DoesNotExist", Description = "x", ExampleUtterance = "x" }]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*DoesNotExist*");
    }

    [Fact]
    public void Validate_MissingRequiredParameterDescription_Throws()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "x", ExampleUtterance = "x" }] // no Parameters described at all
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*speedKts*");
    }

    [Fact]
    public void Validate_FixedParameterCoversRequiredParameter_DoesNotThrow()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig
                {
                    Operation = "SetSpeed",
                    Description = "x",
                    ExampleUtterance = "x",
                    Parameters = new Dictionary<string, string> { ["speedKts"] = "the speed" },
                    FixedParameters = new Dictionary<string, string> { ["tailNumber"] = "UAV-1" }
                }
            ]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MissingInstructions_Throws()
    {
        var config = new AgentConfig { Instructions = "" };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Instructions*");
    }

    [Fact]
    public void Validate_ToolMissingDescription_Throws()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "", ExampleUtterance = "x" }]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*SetSpeed*Description*");
    }

    [Fact]
    public void Validate_ToolMissingExampleUtterance_Throws()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools = [new AgentToolConfig { Operation = "SetSpeed", Description = "x", ExampleUtterance = "" }]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*SetSpeed*ExampleUtterance*");
    }

    [Fact]
    public void Validate_OperatorPromptTool_SkipsCatalogResolution()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig
                {
                    Kind = "OperatorPrompt",
                    Operation = "AskOperatorWhichLesson",
                    Description = "Ask which lesson to run.",
                    ExampleUtterance = "x"
                    // Deliberately no Parameters entry and not a real catalog operation —
                    // an OperatorPrompt tool isn't resolved against any catalog, so neither
                    // should trip validation.
                }
            ]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_SimulatorInfraOperation_Resolves()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools = [new AgentToolConfig { Operation = "ListSimulatorLessons", Description = "x", ExampleUtterance = "x" }]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            Tools =
            [
                new AgentToolConfig
                {
                    Operation = "SetSpeed",
                    Description = "x",
                    ExampleUtterance = "x",
                    Parameters = new Dictionary<string, string>
                    {
                        ["tailNumber"] = "the tail number",
                        ["speedKts"] = "the speed"
                    }
                }
            ]
        };

        var act = () => AgentConfigValidator.Validate(config, Catalog(), SimulatorInfraCatalog(), WatchdogCatalog(), WatchdogConfigCatalog(), OpenAi());

        act.Should().NotThrow();
    }
}

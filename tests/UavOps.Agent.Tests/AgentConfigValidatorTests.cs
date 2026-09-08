using FluentAssertions;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests;

public class AgentConfigValidatorTests
{
    private static OpenAiOptions OpenAi() => new();

    [Fact]
    public void Validate_MissingInstructions_Throws()
    {
        var config = new AgentConfig { Instructions = "" };

        var act = () => AgentConfigValidator.Validate(config, OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Instructions*");
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var config = new AgentConfig
        {
            Instructions = "x",
            McpServers = [new McpServerConfig { Name = "moav", Command = "dotnet", Args = ["exec", "x.dll"] }]
        };

        var act = () => AgentConfigValidator.Validate(config, OpenAi());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownProvider_Throws()
    {
        var config = new AgentConfig { Instructions = "x", Provider = "Anthropic" };

        var act = () => AgentConfigValidator.Validate(config, OpenAi());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Anthropic*");
    }

    [Fact]
    public void Validate_OpenAiProviderWithoutApiKey_Throws()
    {
        var config = new AgentConfig { Instructions = "x", Provider = "OpenAI" };

        var act = () => AgentConfigValidator.Validate(config, new OpenAiOptions { ApiKey = null });

        act.Should().Throw<InvalidOperationException>().WithMessage("*API key*");
    }
}

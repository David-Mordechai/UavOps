using FluentAssertions;
using UavOps.Agent.Options;
using Xunit;

namespace UavOps.Agent.Tests;

public class AgentConfigValidatorTests
{
    [Fact]
    public void Validate_MissingInstructions_Throws()
    {
        var config = new AgentConfig { Instructions = "" };

        var act = () => AgentConfigValidator.Validate(config);

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

        var act = () => AgentConfigValidator.Validate(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownProvider_Throws()
    {
        var config = new AgentConfig { Instructions = "x", Provider = "Anthropic" };

        var act = () => AgentConfigValidator.Validate(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Anthropic*");
    }

    [Fact]
    public void Validate_OpenAiProviderWithoutApiKey_DoesNotThrow()
    {
        // A self-hosted OpenAI-compatible server (vLLM, llama.cpp) doesn't require a real key —
        // only a genuine OpenAI/OpenRouter-style endpoint does, and that's the operator's own
        // responsibility to configure correctly, not something worth failing startup over.
        var config = new AgentConfig { Instructions = "x", Provider = "OpenAI" };

        var act = () => AgentConfigValidator.Validate(config);

        act.Should().NotThrow();
    }
}

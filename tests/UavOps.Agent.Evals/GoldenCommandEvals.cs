using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UavOps.Agent.Evals;

/// <summary>
/// Drives the real, live UavOps.Agent chat pipeline (Ollama + UavOps.ControlApi must already be
/// running — see LiveDependenciesFixture) with a golden set of utterances. Asserts only what's
/// objectively checkable (which tool got called, with which arguments); prints the full
/// transcript of every case regardless of pass/fail so a human (or Claude, on request) can judge
/// response quality — see eval/golden-commands/README for how this suite is meant to be used.
/// </summary>
public sealed class GoldenCommandEvals(LiveDependenciesFixture fixture, ITestOutputHelper output) : IClassFixture<LiveDependenciesFixture>
{
    private const string HubUrl = "http://localhost:5262/chatHub";

    public static IEnumerable<object[]> GoldenCases() => LoadCases().Select(c => new object[] { c });

    private static List<EvalCase> LoadCases([CallerFilePath] string sourceFile = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "eval", "golden-commands");
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var cases = new List<EvalCase>();
        foreach (var file in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f))
        {
            cases.AddRange(deserializer.Deserialize<List<EvalCase>>(File.ReadAllText(file)));
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task GoldenCase(EvalCase testCase)
    {
        _ = fixture; // constructor injection is what forced LiveDependenciesFixture.InitializeAsync to run first

        // Arrange
        var trace = new List<(string Agent, string Tool, string Args)>();
        string? finalResponse = null;

        await using var connection = new HubConnectionBuilder().WithUrl(HubUrl).Build();

        connection.On<string, string, string, string, string, double>("ReceiveAgentTrace",
            (_, agent, tool, args, _, _) => trace.Add((agent, tool, args)));

        connection.On<string, string, double, string>("ReceiveChatMessage", (user, text, _, _) =>
        {
            if (user != "Operator")
            {
                finalResponse = text;
            }
        });

        connection.On<string, string, string, string, string>("ReceiveConfirmationRequest",
            (confirmationId, _, _, _, _) => connection.InvokeAsync("SendConfirmationResponse", confirmationId, true));

        await connection.StartAsync();

        // Act
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        await connection.InvokeAsync("SendMessage", "Operator", testCase.Utterance, correlationId);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (finalResponse is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await connection.StopAsync();

        // Print the full transcript regardless of pass/fail — this is what gets reviewed on request.
        output.WriteLine($"=== {testCase.Name} ===");
        output.WriteLine($"Utterance: {testCase.Utterance}");
        foreach (var step in trace)
        {
            output.WriteLine($"  [{step.Agent}] {step.Tool}({step.Args})");
        }
        output.WriteLine($"Final response: {finalResponse}");
        output.WriteLine("");

        // Assert — only the objectively-checkable part.
        finalResponse.Should().NotBeNull($"case '{testCase.Name}' should complete within 60s");

        if (testCase.ExpectNoToolCalls)
        {
            // MainAgent's own delegation-to-a-domain-agent step is always logged as a trace
            // entry too (agent="MainAgent"), even when that domain agent goes on to decline and
            // ask a clarifying question rather than calling an actual UAV-API tool. Only entries
            // logged BY a domain agent represent a real UAV-API tool call.
            var actualToolCalls = trace.Where(t => t.Agent != "MainAgent").ToList();
            actualToolCalls.Should().BeEmpty($"case '{testCase.Name}' expects no UAV-API tool calls (ambiguous/clarification scenario)");
            return;
        }

        foreach (var expected in testCase.ExpectedTools)
        {
            var matches = trace.Where(t => t.Agent == expected.Agent && t.Tool == expected.Tool).ToList();
            matches.Should().NotBeEmpty($"expected {expected.Agent}.{expected.Tool} to be called for case '{testCase.Name}'");

            var call = matches[0];
            foreach (var (key, expectedValue) in expected.ArgsContain)
            {
                call.Args.Should().Contain(expectedValue, $"expected arg '{key}'='{expectedValue}' in {expected.Tool} call for '{testCase.Name}'");
            }
        }
    }
}

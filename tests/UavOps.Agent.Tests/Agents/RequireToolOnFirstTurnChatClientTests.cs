using FluentAssertions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents;
using Xunit;

namespace UavOps.Agent.Tests.Agents;

public class RequireToolOnFirstTurnChatClientTests
{
    private sealed class FakeInnerChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatOptions OptionsWithOneTool() => new()
    {
        Tools = [AIFunctionFactory.Create(() => "result", "SomeTool")]
    };

    [Fact]
    public async Task GetResponseAsync_NoToolMessageInHistory_ForcesRequireAny()
    {
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "fly UAV-1 to alpha")], OptionsWithOneTool());

        inner.LastOptions!.ToolMode.Should().Be(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task GetResponseAsync_ToolMessageAlreadyInHistory_PassesThroughOriginalToolMode()
    {
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);
        var options = OptionsWithOneTool();
        options.ToolMode = ChatToolMode.Auto;

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "fly UAV-1 to alpha"),
                new ChatMessage(ChatRole.Assistant, "calling Navigate"),
                new ChatMessage(ChatRole.Tool, "navigate result")
            ],
            options);

        inner.LastOptions!.ToolMode.Should().Be(ChatToolMode.Auto);
    }

    [Fact]
    public async Task GetResponseAsync_OriginalOptionsUnmodified_NotMutatedInPlace()
    {
        // The caller's own ChatOptions instance must never be mutated - AgentFactory reuses the
        // same ChatOptions object across every call for a given agent.
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);
        var options = OptionsWithOneTool();
        options.ToolMode = ChatToolMode.Auto;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "fly UAV-1 to alpha")], options);

        options.ToolMode.Should().Be(ChatToolMode.Auto);
    }

    [Fact]
    public async Task GetResponseAsync_NoToolsConfigured_DoesNotForceToolMode()
    {
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);
        var options = new ChatOptions { Tools = [] };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions!.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task GetResponseAsync_NullOptions_DoesNotThrow()
    {
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null);

        inner.LastOptions.Should().BeNull();
    }

    [Fact]
    public async Task GetResponseAsync_ToolMessageOnlyFromAnEarlierTurn_StillForcesRequireAnyForNewTurn()
    {
        // The turn-scoping fix: a persistent, multi-turn agent (BrainAgent) has real tool messages
        // from earlier turns permanently in its history - those must not be mistaken for "this new
        // message has already been acted on". Only a tool message *after* the most recent user
        // message counts.
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "set UAV-1 speed to 210"),
                new ChatMessage(ChatRole.Assistant, "calling CreatePlan"),
                new ChatMessage(ChatRole.Tool, "plan executed"),
                new ChatMessage(ChatRole.Assistant, "UAV-1 speed set to 210."),
                new ChatMessage(ChatRole.User, "set UAV-1 speed to 250") // new turn, nothing called for it yet
            ],
            OptionsWithOneTool());

        inner.LastOptions!.ToolMode.Should().Be(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_NoToolMessageInHistory_ForcesRequireAny()
    {
        var inner = new FakeInnerChatClient();
        var client = new RequireToolOnFirstTurnChatClient(inner);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "fly UAV-1 to alpha")], OptionsWithOneTool()))
        {
        }

        inner.LastOptions!.ToolMode.Should().Be(ChatToolMode.RequireAny);
    }
}

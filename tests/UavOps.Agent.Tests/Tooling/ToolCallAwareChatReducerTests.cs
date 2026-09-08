using FluentAssertions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests.Tooling;

public class ToolCallAwareChatReducerTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);
    private static ChatMessage System(string text) => new(ChatRole.System, text);

    private static ChatMessage AssistantToolCall(string callId, string toolName) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())]);

    private static ChatMessage ToolResult(string callId, object? result) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private static List<ChatMessage> OneFullTurn(string userText, string callId, string toolName, string finalText) =>
    [
        User(userText),
        AssistantToolCall(callId, toolName),
        ToolResult(callId, "ok"),
        AssistantText(finalText)
    ];

    [Fact]
    public async Task ReduceAsync_KeepsSystemMessageFirst_EvenWhenTargetIsSmall()
    {
        var reducer = new ToolCallAwareChatReducer(targetMessageCount: 2);
        var messages = new List<ChatMessage> { System("instructions") };
        messages.AddRange(OneFullTurn("hi", "call-1", "ListFleet", "done"));

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        result.Should().Contain(m => m.Role == ChatRole.System);
        result[0].Role.Should().Be(ChatRole.System);
    }

    [Fact]
    public async Task ReduceAsync_NeverStripsFunctionCallOrResultContent_FromAKeptTurn()
    {
        // This is the exact defect found in Microsoft.Extensions.AI's own MessageCountingChatReducer:
        // it unconditionally excludes any message containing FunctionCallContent/FunctionResultContent,
        // regardless of the target count - see ToolCallAwareChatReducer's own doc comment. A kept turn
        // must carry its tool call and tool result through untouched.
        var reducer = new ToolCallAwareChatReducer(targetMessageCount: 100);
        var messages = OneFullTurn("fly UAV-1 to alpha", "call-1", "Navigate", "Done, UAV-1 is en route.");

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        result.Should().Contain(m => m.Contents.Any(c => c is FunctionCallContent));
        result.Should().Contain(m => m.Contents.Any(c => c is FunctionResultContent));
    }

    [Fact]
    public async Task ReduceAsync_NeverOrphansAToolCallFromItsResult_AtATurnBoundary()
    {
        // Ten turns, tiny budget - forces the reducer to drop most of them. Every turn that survives
        // must survive whole: a tool-call message can never appear without its matching tool-result
        // message (or vice versa) also surviving, since that's exactly the corruption this class
        // exists to prevent.
        var reducer = new ToolCallAwareChatReducer(targetMessageCount: 5);
        var messages = new List<ChatMessage> { System("instructions") };
        for (var i = 0; i < 10; i++)
        {
            messages.AddRange(OneFullTurn($"turn {i}", $"call-{i}", "Navigate", $"done {i}"));
        }

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        var callIds = result.OfType<ChatMessage>()
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToList();
        var resultIds = result.OfType<ChatMessage>()
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(c => c.CallId)
            .ToList();

        callIds.Should().BeEquivalentTo(resultIds, "every surviving tool call must keep its matching result, and vice versa");
        callIds.Should().NotBeEmpty("at least the most recent turn's tool call must survive");
    }

    [Fact]
    public async Task ReduceAsync_AlwaysKeepsTheMostRecentTurnInFull_EvenIfItAloneExceedsTheTarget()
    {
        var reducer = new ToolCallAwareChatReducer(targetMessageCount: 1);
        var messages = OneFullTurn("fly them all", "call-1", "Navigate", "done"); // 4 messages, over budget alone

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        result.Should().HaveCount(4, "the single most recent turn must never be truncated mid-turn");
    }

    [Fact]
    public async Task ReduceAsync_DropsOldestTurnsFirst_OnceOverBudget()
    {
        var reducer = new ToolCallAwareChatReducer(targetMessageCount: 3); // each turn is 4 messages, so the most recent turn alone already exceeds this
        var messages = new List<ChatMessage>();
        messages.AddRange(OneFullTurn("oldest", "call-old", "Navigate", "done old"));
        messages.AddRange(OneFullTurn("newest", "call-new", "Navigate", "done new"));

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        result.Should().NotContain(m => m.Text == "oldest");
        result.Should().Contain(m => m.Text == "newest");
    }

    [Fact]
    public async Task ReduceAsync_PlainTextConversation_StillBoundedByTargetCount()
    {
        var reducer = new ToolCallAwareChatReducer(targetMessageCount: 4);
        var messages = new List<ChatMessage>
        {
            User("hi"), AssistantText("hello"),
            User("how are you"), AssistantText("good"),
            User("bye"), AssistantText("goodbye"),
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        result.Should().NotContain(m => m.Text == "hi");
        result.Should().Contain(m => m.Text == "bye");
    }
}

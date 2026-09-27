using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using UavOps.Agent.Hubs;
using UavOps.Agent.Tooling;
using UavOps.Agent.Voice;

namespace UavOps.Agent.Tests.Hubs;

/// <summary>
/// The host's side of anything unprompted: fleet reports are forwarded to McpMoav unread, and an
/// MCP server's message is delivered as given. Both are gated on who's calling. What the messages
/// say is each domain's business and tested there (e.g. MissionEventServiceTests).
/// </summary>
public class ChatHubUnpromptedMessageTests
{
    private readonly IHubContext<ChatHub> _hubContext = Substitute.For<IHubContext<ChatHub>>();
    private readonly IClientProxy _allClients = Substitute.For<IClientProxy>();
    private readonly IClientProxy _moavRelay = Substitute.For<IClientProxy>();
    private readonly ProactiveHistoryJournal _journal = new();

    public ChatHubUnpromptedMessageTests()
    {
        _hubContext.Clients.All.Returns(_allClients);
        _hubContext.Clients.Group("moav-relay").Returns(_moavRelay);
    }

    private static DetectionReport Van() =>
        new("997", "m1", "ZoneA", "white van", "van", 0.87, 31.81234, 34.66123, DateTime.UtcNow, "t1");

    private List<string> PushedChatMessages() =>
        _allClients.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync) && (string)c.GetArguments()[0]! == "ReceiveChatMessage")
            .Select(c => (string)((object?[])c.GetArguments()[1]!)[1]!)
            .ToList();

    [Theory]
    [InlineData("/chatHub", null)]
    [InlineData("/uavCommandHub", "relay")]
    public async Task ReportDetection_FromAnythingButTheFleetConnection_IsRejected(string path, string? clientQuery)
    {
        var act = () => CreateHub(path, clientQuery).ReportDetection(Van());

        await act.Should().ThrowAsync<HubException>();
        _moavRelay.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ReportDetection_FromTheFleetConnection_IsForwardedToMcpMoavUnchanged()
    {
        var report = Van();

        await CreateHub("/uavCommandHub", null).ReportDetection(report);

        await _moavRelay.Received(1).SendCoreAsync(HostHubContract.FleetEvents.Detection,
            Arg.Is<object?[]>(a => a != null && a.Length == 1 && ReferenceEquals(a[0], report)), Arg.Any<CancellationToken>());
        PushedChatMessages().Should().BeEmpty("the host doesn't decide what a detection means");
        _journal.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task ReportMissionEvent_FromTheFleetConnection_IsForwardedToMcpMoavUnchanged()
    {
        var report = new MissionEventReport("997", "m1", "ZoneA", MissionEventKinds.Completed);

        await CreateHub("/uavCommandHub", null).ReportMissionEvent(report);

        await _moavRelay.Received(1).SendCoreAsync(HostHubContract.FleetEvents.MissionEvent,
            Arg.Is<object?[]>(a => a != null && a.Length == 1 && ReferenceEquals(a[0], report)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/chatHub", null)]
    [InlineData("/uavCommandHub", null)]
    public async Task PostOperatorMessage_FromAnythingButAnMcpServer_IsRejected(string path, string? clientQuery)
    {
        var act = () => CreateHub(path, clientQuery).PostOperatorMessage("hello", "a note");

        await act.Should().ThrowAsync<HubException>();
        PushedChatMessages().Should().BeEmpty();
        _journal.Drain().Should().BeEmpty();
    }

    [Theory]
    [InlineData("/chatHub")]       // McpSimulator
    [InlineData("/uavCommandHub")] // McpMoav's relay client
    public async Task PostOperatorMessage_FromAnMcpServer_IsShownAsGiven_AndJoinsHistory(string path)
    {
        await CreateHub(path, "relay").PostOperatorMessage("White van detected.", "UAV 997 reported a detection.");

        PushedChatMessages().Should().Equal("White van detected.");
        var history = _journal.Drain();
        history.Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant);
        history[0].Text.Should().Be(ProactiveHistoryJournal.NotePrefix + "UAV 997 reported a detection.");
        history[1].Text.Should().Be("White van detected.");
    }

    [Fact]
    public async Task PostOperatorMessage_WithoutANote_LeavesHistoryAlone()
    {
        await CreateHub("/chatHub", "relay").PostOperatorMessage("Just so you know.", null);

        PushedChatMessages().Should().Equal("Just so you know.");
        _journal.Drain().Should().BeEmpty();
    }

    [Fact]
    public void AddHistoryNote_OnlyTouchesHistory()
    {
        CreateHub("/uavCommandHub", "relay").AddHistoryNote("UAV 997 stopped searching.", "997 stopped searching ZoneA.");

        PushedChatMessages().Should().BeEmpty();
        _journal.Drain().Should().HaveCount(2);
    }

    [Fact]
    public async Task PostPhrasedOperatorMessage_FromAChatTab_IsRejected()
    {
        var act = () => CreateHub("/chatHub", null).PostPhrasedOperatorMessage("Say hi.", 0);

        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task HistoryNoteTurn_SurvivesReductionAsAWhole()
    {
        _journal.Add("UAV 997 reported a detection.", "White van detected.");
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        for (var i = 0; i < 10; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"question {i}"));
            history.Add(new ChatMessage(ChatRole.Assistant, $"answer {i}"));
        }
        history.AddRange(_journal.Drain());

        var reduced = (await new ToolCallAwareChatReducer(targetMessageCount: 4).ReduceAsync(history, CancellationToken.None)).ToList();

        reduced[^2].Text.Should().StartWith(ProactiveHistoryJournal.NotePrefix);
        reduced[^1].Text.Should().Be("White van detected.");
    }

    private ChatHub CreateHub(string path, string? clientQuery)
    {
        var confirmationGate = new ConfirmationGate(_hubContext, new ConfigurationBuilder().Build(), NullLogger<ConfirmationGate>.Instance);
        var hub = new ChatHub(null!, confirmationGate, new OperatorPromptGate(_hubContext, NullLogger<OperatorPromptGate>.Instance),
            new ToolInvocationLogger(NullLogger<ToolInvocationLogger>.Instance, _hubContext), Substitute.For<IRemoteOperationBroker>(),
            _hubContext, null!, new PushToTalkRouter(_hubContext, NullLogger<PushToTalkRouter>.Instance), _journal,
            NullLogger<ChatHub>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (clientQuery is not null)
            httpContext.Request.QueryString = new QueryString($"?client={clientQuery}");
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
        var context = Substitute.For<HubCallerContext>();
        context.Features.Returns(features);
        context.ConnectionId.Returns("conn-1");
        hub.Context = context;
        return hub;
    }

    private sealed class HttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}

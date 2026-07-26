using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.OpenApi.Models;
using UavOps.Agent.Tooling;
using Xunit;

namespace UavOps.Agent.Tests;

public class UavApiToolInvokerTests
{
    /// <summary>Captures the outgoing request instead of making a real network call.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static (UavApiToolInvoker Invoker, CapturingHandler Handler) CreateSut()
    {
        var handler = new CapturingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5250") };
        return (new UavApiToolInvoker(client), handler);
    }

    [Fact]
    public async Task InvokeAsync_SubstitutesPathParameter()
    {
        var (invoker, handler) = CreateSut();
        var descriptor = new UavApiOperationDescriptor(
            "GetTelemetry", OperationType.Get, "/uavs/{tailNumber}/telemetry",
            [new UavApiParameterDescriptor("tailNumber", ParamLocation.Path, true, new OpenApiSchema { Type = "string" })]);

        await invoker.InvokeAsync(descriptor, new Dictionary<string, object?> { ["tailNumber"] = "UAV-2" }, CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/uavs/UAV-2/telemetry");
        handler.LastRequest.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task InvokeAsync_BuildsQueryString_FromQueryParameters()
    {
        var (invoker, handler) = CreateSut();
        var descriptor = new UavApiOperationDescriptor(
            "Search", OperationType.Get, "/search",
            [new UavApiParameterDescriptor("q", ParamLocation.Query, false, new OpenApiSchema { Type = "string" })]);

        await invoker.InvokeAsync(descriptor, new Dictionary<string, object?> { ["q"] = "target alpha" }, CancellationToken.None);

        handler.LastRequest!.RequestUri!.Query.Should().Be("?q=target%20alpha");
    }

    [Fact]
    public async Task InvokeAsync_OmitsQueryParameter_WhenValueIsNull()
    {
        var (invoker, handler) = CreateSut();
        var descriptor = new UavApiOperationDescriptor(
            "Search", OperationType.Get, "/search",
            [new UavApiParameterDescriptor("q", ParamLocation.Query, false, new OpenApiSchema { Type = "string" })]);

        await invoker.InvokeAsync(descriptor, new Dictionary<string, object?>(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_BuildsJsonBody_FromBodyProperties_AndExcludesPathParameters()
    {
        var (invoker, handler) = CreateSut();
        var descriptor = new UavApiOperationDescriptor(
            "SetSpeed", OperationType.Post, "/uavs/{tailNumber}/speed",
            [
                new UavApiParameterDescriptor("tailNumber", ParamLocation.Path, true, new OpenApiSchema { Type = "string" }),
                new UavApiParameterDescriptor("speedKts", ParamLocation.BodyProperty, true, new OpenApiSchema { Type = "integer" })
            ]);

        await invoker.InvokeAsync(
            descriptor,
            new Dictionary<string, object?> { ["tailNumber"] = "UAV-1", ["speedKts"] = 200 },
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/uavs/UAV-1/speed");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);

        var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("speedKts").GetInt32().Should().Be(200);
        body.RootElement.TryGetProperty("tailNumber", out _).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ReturnsStatusCodeAndBody()
    {
        var (invoker, _) = CreateSut();
        var descriptor = new UavApiOperationDescriptor(
            "GetTelemetry", OperationType.Get, "/uavs/{tailNumber}/telemetry",
            [new UavApiParameterDescriptor("tailNumber", ParamLocation.Path, true, new OpenApiSchema { Type = "string" })]);

        var result = await invoker.InvokeAsync(descriptor, new Dictionary<string, object?> { ["tailNumber"] = "UAV-1" }, CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.Body.Should().Be("{}");
    }
}

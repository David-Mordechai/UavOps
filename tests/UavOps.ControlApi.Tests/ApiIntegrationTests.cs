using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using UavOps.ControlApi.Models;
using Xunit;

namespace UavOps.ControlApi.Tests;

/// <summary>
/// Full in-memory HTTP pipeline (routing, model binding, validation) against a fresh
/// WebApplicationFactory per test — xUnit creates a new class instance per test method, so each
/// test gets its own singleton service instances with no cross-test state leakage.
/// </summary>
public class ApiIntegrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public ApiIntegrationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ListUavs_ReturnsThreeSeededVehicles()
    {
        var response = await _client.GetAsync("/uavs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<UavSummary>>();
        body.Should().HaveCount(3);
    }

    [Fact]
    public async Task NavigateTo_KnownLocation_UpdatesPosition()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/navigate", new NavigateRequest("target alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.Mode.Should().Be("Transiting");
    }

    [Fact]
    public async Task NavigateTo_UnknownLocation_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/navigate", new NavigateRequest("nowhere"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetSpeed_ValidRange_Succeeds()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/speed", new SpeedRequest(250));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.SpeedKts.Should().Be(250);
    }

    [Fact]
    public async Task SetSpeed_OutOfRange_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/speed", new SpeedRequest(9999));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetAltitude_ValidRange_Succeeds()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/altitude", new AltitudeRequest(5000));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.AltitudeFt.Should().Be(5000);
    }

    [Fact]
    public async Task SetAltitude_OutOfRange_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/altitude", new AltitudeRequest(-1));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReturnToLaunch_SetsReturningMode()
    {
        var response = await _client.PostAsync("/uavs/UAV-1/rtl", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.Mode.Should().Be("ReturningToLaunch");
    }

    [Fact]
    public async Task GetTelemetry_ReturnsCurrentState()
    {
        var response = await _client.GetAsync("/uavs/UAV-1/telemetry");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.SpeedKts.Should().Be(105);
    }

    [Fact]
    public async Task PointPayload_KnownLocation_Succeeds()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/payload/point", new PointPayloadRequest("target bravo"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.PayloadLockedOn.Should().Be("target bravo");
    }

    [Fact]
    public async Task PointPayload_UnknownLocation_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/payload/point", new PointPayloadRequest("nowhere"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPayload_ClearsLock()
    {
        await _client.PostAsJsonAsync("/uavs/UAV-1/payload/point", new PointPayloadRequest("home"));

        var response = await _client.PostAsync("/uavs/UAV-1/payload/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TelemetrySnapshot>();
        body!.PayloadLockedOn.Should().BeNull();
    }

    [Fact]
    public async Task UploadWaypoints_StoresWaypointsAndReturnsAcceptedCount()
    {
        var waypoints = new UploadWaypointsRequest([new Waypoint(31.8, 34.6, 3000), new Waypoint(31.9, 34.7, 3500)]);

        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/mission/waypoints", waypoints);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMissionStatus_ReturnsWaypointCount()
    {
        var waypoints = new UploadWaypointsRequest([new Waypoint(31.8, 34.6, 3000)]);
        await _client.PostAsJsonAsync("/uavs/UAV-1/mission/waypoints", waypoints);

        var response = await _client.GetAsync("/uavs/UAV-1/mission/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MissionStatus>();
        body!.WaypointCount.Should().Be(1);
    }

    [Fact]
    public async Task GetLinkStatus_ReturnsStatus()
    {
        var response = await _client.GetAsync("/uavs/UAV-1/gdt/link-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GdtLinkStatus>();
        body!.LinkState.Should().Be("Connected");
    }

    [Fact]
    public async Task SetAntennaTrackingMode_ValidMode_Succeeds()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/gdt/tracking-mode", new SetTrackingModeRequest("Manual"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GdtLinkStatus>();
        body!.TrackingMode.Should().Be("Manual");
    }

    [Fact]
    public async Task SetAntennaTrackingMode_InvalidMode_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/uavs/UAV-1/gdt/tracking-mode", new SetTrackingModeRequest("Bogus"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public static IEnumerable<object[]> UnknownTailNumberRoutes()
    {
        yield return ["POST", "/uavs/UAV-99/navigate", new NavigateRequest("home")];
        yield return ["POST", "/uavs/UAV-99/speed", new SpeedRequest(100)];
        yield return ["POST", "/uavs/UAV-99/altitude", new AltitudeRequest(1000)];
        yield return ["POST", "/uavs/UAV-99/rtl", null!];
        yield return ["GET", "/uavs/UAV-99/telemetry", null!];
        yield return ["POST", "/uavs/UAV-99/payload/point", new PointPayloadRequest("home")];
        yield return ["POST", "/uavs/UAV-99/payload/reset", null!];
        yield return ["POST", "/uavs/UAV-99/mission/waypoints", new UploadWaypointsRequest([])];
        yield return ["GET", "/uavs/UAV-99/mission/status", null!];
        yield return ["GET", "/uavs/UAV-99/gdt/link-status", null!];
        yield return ["POST", "/uavs/UAV-99/gdt/tracking-mode", new SetTrackingModeRequest("Auto")];
    }

    [Theory]
    [MemberData(nameof(UnknownTailNumberRoutes))]
    public async Task AnyRoute_UnknownTailNumber_Returns404(string method, string path, object? body)
    {
        var response = method == "GET"
            ? await _client.GetAsync(path)
            : await _client.PostAsJsonAsync(path, body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, because: $"{method} {path} should 404 for an unknown tail number");
    }
}

using Microsoft.AspNetCore.Mvc;
using UavOps.ControlApi.Models;
using UavOps.ControlApi.Services;

namespace UavOps.ControlApi.Controllers;

[ApiController]
[Route("uavs/{tailNumber}")]
public sealed class UavController(IUavFleetService fleetService) : ControllerBase
{
    [HttpPost("navigate")]
    [EndpointName("NavigateTo")]
    public IActionResult Navigate(string tailNumber, NavigateRequest request) =>
        ToActionResult(fleetService.Navigate(tailNumber, request.Location));

    [HttpPost("speed")]
    [EndpointName("SetSpeed")]
    public IActionResult SetSpeed(string tailNumber, SpeedRequest request) =>
        ToActionResult(fleetService.SetSpeed(tailNumber, request.SpeedKts));

    [HttpPost("altitude")]
    [EndpointName("SetAltitude")]
    public IActionResult SetAltitude(string tailNumber, AltitudeRequest request) =>
        ToActionResult(fleetService.SetAltitude(tailNumber, request.AltitudeFt));

    [HttpPost("rtl")]
    [EndpointName("ReturnToLaunch")]
    public IActionResult ReturnToLaunch(string tailNumber) =>
        ToActionResult(fleetService.ReturnToLaunch(tailNumber));

    [HttpGet("telemetry")]
    [EndpointName("GetTelemetry")]
    public IActionResult GetTelemetry(string tailNumber) =>
        ToActionResult(fleetService.GetTelemetry(tailNumber));

    [HttpPost("payload/point")]
    [EndpointName("PointPayload")]
    public IActionResult PointPayload(string tailNumber, PointPayloadRequest request) =>
        ToActionResult(fleetService.PointPayload(tailNumber, request.Location));

    [HttpPost("payload/reset")]
    [EndpointName("ResetPayload")]
    public IActionResult ResetPayload(string tailNumber) =>
        ToActionResult(fleetService.ResetPayload(tailNumber));

    [HttpPost("mission/waypoints")]
    [EndpointName("UploadWaypoints")]
    public IActionResult UploadWaypoints(string tailNumber, UploadWaypointsRequest request)
    {
        var (found, accepted) = fleetService.UploadWaypoints(tailNumber, request.Waypoints);
        if (!found)
        {
            return NotFound(new { error = $"Unknown UAV tail number '{tailNumber}'." });
        }

        return Ok(new { accepted });
    }

    [HttpGet("mission/status")]
    [EndpointName("GetMissionStatus")]
    public IActionResult GetMissionStatus(string tailNumber)
    {
        var (found, status) = fleetService.GetMissionStatus(tailNumber);
        if (!found)
        {
            return NotFound(new { error = $"Unknown UAV tail number '{tailNumber}'." });
        }

        return Ok(status);
    }

    private IActionResult ToActionResult(UavCommandResult result) => result.Error switch
    {
        UavCommandError.None => Ok(result.Snapshot),
        UavCommandError.VehicleNotFound => NotFound(new { error = result.ErrorMessage }),
        _ => BadRequest(new { error = result.ErrorMessage })
    };
}

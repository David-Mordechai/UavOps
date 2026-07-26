using Microsoft.AspNetCore.Mvc;
using UavOps.ControlApi.Models;
using UavOps.ControlApi.Services;

namespace UavOps.ControlApi.Controllers;

[ApiController]
[Route("uavs/{tailNumber}/gdt")]
public sealed class GdtController(IGdtService gdtService) : ControllerBase
{
    [HttpGet("link-status")]
    [EndpointName("GetLinkStatus")]
    public IActionResult GetLinkStatus(string tailNumber)
    {
        var (found, status) = gdtService.GetLinkStatus(tailNumber);
        if (!found)
        {
            return NotFound(new { error = $"Unknown UAV tail number '{tailNumber}'." });
        }

        return Ok(status);
    }

    [HttpPost("tracking-mode")]
    [EndpointName("SetAntennaTrackingMode")]
    public IActionResult SetTrackingMode(string tailNumber, SetTrackingModeRequest request)
    {
        var (found, status, error) = gdtService.SetTrackingMode(tailNumber, request.Mode);
        if (!found)
        {
            return NotFound(new { error = $"Unknown UAV tail number '{tailNumber}'." });
        }

        if (error is not null)
        {
            return BadRequest(new { error });
        }

        return Ok(status);
    }
}

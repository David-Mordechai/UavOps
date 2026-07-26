using Microsoft.AspNetCore.Mvc;
using UavOps.ControlApi.Services;

namespace UavOps.ControlApi.Controllers;

[ApiController]
[Route("uavs")]
public sealed class FleetController(IUavFleetService fleetService) : ControllerBase
{
    [HttpGet]
    [EndpointName("ListUavs")]
    public IActionResult ListUavs() => Ok(fleetService.ListFleet());
}

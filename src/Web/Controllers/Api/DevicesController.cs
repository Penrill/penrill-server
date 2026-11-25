using Application.Linking.Commands.RevokeDevice;
using Application.Linking.Queries.GetDevices;
using Microsoft.AspNetCore.Authorization;

namespace Web.Controllers.Api;

[Authorize]
[Route("api/devices")]
public class DevicesController : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeviceItemDto>>> GetDevices(CancellationToken ct) =>
        Ok(await Mediator.Send(new GetDevicesQuery(), ct));

    [HttpPost("{id}/revoke")]
    public async Task<IActionResult> RevokeDevice(string id, CancellationToken ct)
    {
        await Mediator.Send(new RevokeDeviceCommand(id), ct);
        return NoContent();
    }
}

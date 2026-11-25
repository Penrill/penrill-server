using Microsoft.AspNetCore.Authorization;

namespace Web.Controllers;

[ApiController]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { service = "penrill-server" });
}

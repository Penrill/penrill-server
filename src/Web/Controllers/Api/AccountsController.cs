using Application.Accounts;
using Application.Accounts.Commands.Register;
using Application.Accounts.Commands.SignIn;
using Application.Accounts.Commands.SignOut;
using Application.Accounts.Queries.GetServerInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Web.Controllers.Api;

[Authorize]
[Route("api")]
public class AccountsController : ApiControllerBase
{
    public sealed record Credentials(string? Name, string? Password);

    public sealed record ServerResponse(string Service, string Name, string Registration, bool Link, string? Pin, string Edition, ServerCapabilities Capabilities);

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("register")]
    public async Task<ActionResult<SignedIn>> Register([FromBody] Credentials input, CancellationToken ct) =>
        Ok(await Mediator.Send(new RegisterCommand(input.Name, input.Password), ct));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("login")]
    public async Task<ActionResult<SignedIn>> Login([FromBody] Credentials input, CancellationToken ct) =>
        Ok(await Mediator.Send(new SignInCommand(input.Name, input.Password), ct));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await Mediator.Send(new SignOutCommand(), ct);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("server")]
    public async Task<ActionResult<ServerResponse>> GetServer([FromServices] IPublicAddress address, CancellationToken ct)
    {
        var info = await Mediator.Send(new GetServerInfoQuery(), ct);
        return Ok(new ServerResponse("penrill", info.Name, info.OpenRegistration ? "open" : "invite", true, address.PinFor(Request), "server", new ServerCapabilities()));
    }
}

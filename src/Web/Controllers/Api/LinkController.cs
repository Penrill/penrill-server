using Application.Linking;
using Application.Linking.Commands.AnswerLink;
using Application.Linking.Commands.PollLink;
using Application.Linking.Commands.StartLink;
using Application.Linking.Queries.GetLinkRequest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Web.Controllers.Api;

[Authorize]
[Route("api/link")]
public class LinkController : ApiControllerBase
{
    public sealed record StartBody(string? Device, string? App);

    public sealed record PollBody(string? DeviceCode);

    public sealed record DecideBody(bool Allow);

    public sealed record StartResponse(string DeviceCode, string UserCode, string VerifyUrl, string VerifyUrlComplete, int ExpiresIn);

    public sealed record RequestResponse(string Code, string Device, string App, string Address, DateTimeOffset Created);

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("start")]
    public async Task<ActionResult<StartResponse>> StartLink([FromBody] StartBody body, [FromServices] IPublicAddress address, CancellationToken ct)
    {
        var started = await Mediator.Send(new StartLinkCommand(body.Device, body.App, HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""), ct);
        var origin = address.Origin(Request);
        return Ok(new StartResponse(started.DeviceCode, started.UserCode, $"{origin}/link", $"{origin}/link?code={started.UserCode}", started.ExpiresIn));
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.LinkPoll)]
    [HttpPost("poll")]
    public async Task<IActionResult> PollLink([FromBody] PollBody body, CancellationToken ct)
    {
        var result = await Mediator.Send(new PollLinkCommand(body.DeviceCode), ct);
        return result.Status switch
        {
            PollStatus.Approved => Ok(new { name = result.User, token = result.Token }),
            PollStatus.Pending => StatusCode(StatusCodes.Status202Accepted),
            PollStatus.Denied => StatusCode(StatusCodes.Status403Forbidden),
            _ => StatusCode(StatusCodes.Status410Gone)
        };
    }

    [HttpGet("{code}")]
    public async Task<ActionResult<RequestResponse>> GetLinkRequest(string code, CancellationToken ct)
    {
        var info = await Mediator.Send(new GetLinkRequestQuery(code), ct);
        return Ok(new RequestResponse(info.UserCode, info.Device, info.App, info.Address, info.Created));
    }

    [HttpPost("{code}")]
    public async Task<IActionResult> AnswerLink(string code, [FromBody] DecideBody body, CancellationToken ct)
    {
        await Mediator.Send(new AnswerLinkCommand(code, body.Allow), ct);
        return NoContent();
    }
}

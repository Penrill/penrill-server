using Application.Accounts;
using Application.Accounts.Commands.AcceptInvite;
using Application.Accounts.Commands.CreateInvite;
using Application.Accounts.Commands.DeleteInvite;
using Application.Accounts.Queries.CheckInvite;
using Application.Accounts.Queries.GetInvites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Web.Controllers.Api;

[Authorize]
[Route("api/invites")]
public class InvitesController : ApiControllerBase
{
    public sealed record CreateBody(string? Role, int? Uses, int? Days);

    public sealed record AcceptBody(string? Name, string? Password);

    public sealed record CreatedInviteResponse(string Id, string Token, string Url, string Role, int UsesLeft, DateTimeOffset? Expires, string CreatedBy);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InviteDto>>> GetInvites(CancellationToken ct) =>
        Ok(await Mediator.Send(new GetInvitesQuery(), ct));

    [HttpPost]
    public async Task<ActionResult<CreatedInviteResponse>> CreateInvite([FromServices] IPublicAddress address, [FromBody] CreateBody? input = null, CancellationToken ct = default)
    {
        var created = await Mediator.Send(new CreateInviteCommand(input?.Role, input?.Uses, input?.Days), ct);
        var url = $"{address.Origin(Request)}/invite/{created.Token}";
        return Ok(new CreatedInviteResponse(created.Id, created.Token, url, created.Role, created.UsesLeft, created.Expires, created.CreatedBy));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteInvite(string id, CancellationToken ct)
    {
        await Mediator.Send(new DeleteInviteCommand(id), ct);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpGet("{token}")]
    public async Task<IActionResult> CheckInvite(string token, CancellationToken ct)
    {
        await Mediator.Send(new CheckInviteQuery(token), ct);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("{token}")]
    public async Task<ActionResult<SignedIn>> AcceptInvite(string token, [FromBody] AcceptBody input, CancellationToken ct) =>
        Ok(await Mediator.Send(new AcceptInviteCommand(token, input.Name, input.Password), ct));
}

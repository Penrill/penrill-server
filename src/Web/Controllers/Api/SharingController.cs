using Application.Sharing.Commands.AddBoardPerson;
using Application.Sharing.Commands.AddMember;
using Application.Sharing.Commands.CreateFolder;
using Application.Sharing.Commands.DisableDrawLink;
using Application.Sharing.Commands.EnableDrawLink;
using Application.Sharing.Commands.JoinBoard;
using Application.Sharing.Commands.RemoveBoardPerson;
using Application.Sharing.Commands.SetBoardCopying;
using Application.Sharing.Commands.ShareBoard;
using Application.Sharing.Queries.GetBoardPeople;
using Application.Sharing.Queries.GetCommunity;
using Application.Sharing.Queries.GetDrawLink;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Web.Controllers.Api;

[Authorize]
[Route("api")]
public class SharingController : ApiControllerBase
{
    public sealed record FolderChange(string? Name, string? Folder, string? Board);

    public sealed record JoinBody(string? Token, string? Name);

    public sealed record CopyingBody(bool? Allowed);

    [HttpGet("community")]
    public async Task<ActionResult<CommunityDto>> GetCommunity(CancellationToken ct) =>
        Ok(await Mediator.Send(new GetCommunityQuery(), ct));

    [HttpPost("folders")]
    public async Task<IActionResult> CreateFolder([FromBody] FolderChange input, CancellationToken ct)
    {
        await Mediator.Send(new CreateFolderCommand(input.Name), ct);
        return NoContent();
    }

    [HttpPost("members")]
    public async Task<IActionResult> AddMember([FromBody] FolderChange input, CancellationToken ct)
    {
        await Mediator.Send(new AddMemberCommand(input.Folder, input.Name), ct);
        return NoContent();
    }

    [HttpPost("share-board")]
    public async Task<IActionResult> ShareBoard([FromBody] FolderChange input, CancellationToken ct)
    {
        await Mediator.Send(new ShareBoardCommand(input.Folder, input.Board), ct);
        return NoContent();
    }

    [HttpGet("boards/{id}/people")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetBoardPeople(string id, CancellationToken ct) =>
        Ok(await Mediator.Send(new GetBoardPeopleQuery(id), ct));

    [HttpPost("boards/{id}/people")]
    public async Task<IActionResult> AddBoardPerson(string id, [FromBody] FolderChange input, CancellationToken ct)
    {
        await Mediator.Send(new AddBoardPersonCommand(id, input.Name), ct);
        return NoContent();
    }

    [HttpPut("boards/{id}/copying")]
    public async Task<IActionResult> SetBoardCopying(string id, [FromBody] CopyingBody input, CancellationToken ct)
    {
        await Mediator.Send(new SetBoardCopyingCommand(id, input.Allowed), ct);
        return NoContent();
    }

    [HttpDelete("boards/{id}/people/{name}")]
    public async Task<IActionResult> RemoveBoardPerson(string id, string name, CancellationToken ct)
    {
        await Mediator.Send(new RemoveBoardPersonCommand(id, name), ct);
        return NoContent();
    }

    [HttpGet("boards/{id}/link")]
    public async Task<ActionResult<DrawLinkDto>> GetDrawLink(string id, CancellationToken ct) =>
        Ok(await Mediator.Send(new GetDrawLinkQuery(id), ct));

    [HttpPost("boards/{id}/link")]
    public async Task<ActionResult<DrawLinkDto>> EnableDrawLink(string id, CancellationToken ct) =>
        Ok(await Mediator.Send(new EnableDrawLinkCommand(id), ct));

    [HttpDelete("boards/{id}/link")]
    public async Task<IActionResult> DisableDrawLink(string id, CancellationToken ct)
    {
        await Mediator.Send(new DisableDrawLinkCommand(id), ct);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("join")]
    public async Task<ActionResult<JoinedDto>> Join([FromBody] JoinBody input, CancellationToken ct) =>
        Ok(await Mediator.Send(new JoinBoardCommand(input.Token, input.Name), ct));
}

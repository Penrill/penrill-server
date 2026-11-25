using Application.Boards.Commands.CreateBoard;
using Application.Boards.Commands.DeleteBoard;
using Application.Boards.Commands.SubmitChange;
using Application.Boards.Commands.UploadAsset;
using Application.Boards.Queries.GetAsset;
using Application.Boards.Queries.GetBoardChanges;
using Application.Boards.Queries.GetBoardSnapshot;
using Application.Boards.Queries.GetBoards;
using Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;

namespace Web.Controllers.Api;

[Authorize]
[Route("api/boards")]
public class BoardsController : ApiControllerBase
{
    public sealed record SeqResponse(long Seq);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BoardSummaryDto>>> GetBoards(CancellationToken ct) =>
        Ok(await Mediator.Send(new GetBoardsQuery(), ct));

    [HttpPut("{id}")]
    public async Task<IActionResult> CreateBoard(string id, CancellationToken ct)
    {
        if (!Request.HasJsonContentType()) throw new UnsupportedMediaTypeException();
        var content = await Bodies.ReadJsonAsync(Request, BoardLimitPolicy.SelfHostedDefault.MaxBoardBytes, ct);
        var result = await Mediator.Send(new CreateBoardCommand(id, content), ct);
        return result.Created
            ? Created($"/api/boards/{id}", new { seq = result.Seq, created = true })
            : Ok(new { seq = result.Seq, created = false });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetBoard(string id, CancellationToken ct) =>
        File(await Mediator.Send(new GetBoardSnapshotQuery(id), ct), "application/json");

    [HttpGet("{id}/changes")]
    public async Task<IActionResult> GetChanges(string id, [FromQuery] long after, CancellationToken ct) =>
        File(await Mediator.Send(new GetBoardChangesQuery(id, after), ct), "application/json");

    [HttpPost("{id}/changes")]
    public async Task<ActionResult<SeqResponse>> SubmitChange(string id, CancellationToken ct)
    {
        var change = await Bodies.ReadJsonAsync(Request, BoardLimitPolicy.SelfHostedDefault.MaxChangeBytes, ct);
        return Ok(new SeqResponse(await Mediator.Send(new SubmitChangeCommand(id, change), ct)));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteBoard(string id, [FromQuery] string? copies, CancellationToken ct)
    {
        if (copies is not (null or "keep" or "remove")) throw new BadRequestException();
        await Mediator.Send(new DeleteBoardCommand(id, copies == "remove"), ct);
        return NoContent();
    }

    [HttpPut("{id}/assets/{hash}")]
    public async Task<IActionResult> UploadAsset(string id, string hash, CancellationToken ct)
    {
        var type = MediaTypeHeaderValue.TryParse(Request.ContentType, out var media) ? media.MediaType.Value : null;
        var content = await Bodies.ReadAsync(Request, BoardLimitPolicy.SelfHostedDefault.MaxAssetBytes, ct);
        await Mediator.Send(new UploadAssetCommand(id, hash, type, content), ct);
        return NoContent();
    }

    [HttpGet("{id}/assets/{hash}")]
    public async Task<IActionResult> GetAsset(string id, string hash, CancellationToken ct)
    {
        var asset = await Mediator.Send(new GetAssetQuery(id, hash), ct);
        var headers = Response.Headers;
        headers.CacheControl = "private, max-age=31536000, immutable";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return File(asset.Content, asset.ContentType);
    }

    [AllowAnonymous]
    [HttpGet("{id}/live")]
    public async Task Live(string id, [FromServices] LiveSessions sessions)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest || !ProtocolConstants.IsBoardId(id))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await sessions.RunAsync(socket, id);
    }
}

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Penrill.Application.Boards.Commands.CreateBoard;
using Penrill.Application.Boards.Commands.DeleteBoard;
using Penrill.Application.Boards.Commands.SubmitChange;
using Penrill.Application.Boards.Commands.UploadAsset;
using Penrill.Application.Boards.Queries.GetAsset;
using Penrill.Application.Boards.Queries.GetBoardChanges;
using Penrill.Application.Boards.Queries.GetBoardSnapshot;
using Penrill.Application.Boards.Queries.GetBoards;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Live;
using Penrill.Domain.Constants;

namespace Penrill.Web.Endpoints;

/// <summary>Boards, their change log, their images and the live connection.</summary>
public class Boards : IEndpointGroup
{
    public static string? RoutePrefix => "/api/boards";

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapGet(GetBoards);
        groupBuilder.MapPut(CreateBoard, "/{id}");
        groupBuilder.MapGet(GetBoard, "/{id}");
        groupBuilder.MapGet(GetChanges, "/{id}/changes");
        groupBuilder.MapPost(SubmitChange, "/{id}/changes");
        groupBuilder.MapDelete(DeleteBoard, "/{id}");
        groupBuilder.MapPut(UploadAsset, "/{id}/assets/{hash}");
        groupBuilder.MapGet(GetAsset, "/{id}/assets/{hash}");
        // The live socket signs in with its first frame, so the HTTP upgrade itself is anonymous.
        groupBuilder.Map("/{id}/live", Live).AllowAnonymous().WithName(nameof(Live));
    }

    [EndpointSummary("Boards the user can open")]
    public static async Task<Ok<IReadOnlyList<BoardSummaryDto>>> GetBoards(ISender sender) => TypedResults.Ok(await sender.Send(new GetBoardsQuery()));

    [EndpointSummary("Upload a board made on a device")]
    [EndpointDescription("Body {title, background, items}. 201 when created, 200 when it already exists and the user can open it, 409 when it belongs to someone else.")]
    public static async Task<IResult> CreateBoard(ISender sender, string id, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType()) throw new UnsupportedMediaTypeException();
        var content = await Bodies.ReadJsonAsync(request, BoardLimits.MaxBoardBytes, cancellationToken);
        var result = await sender.Send(new CreateBoardCommand(id, content), cancellationToken);
        return result.Created
            ? TypedResults.Created($"/api/boards/{id}", new { seq = result.Seq, created = true })
            : TypedResults.Ok(new { seq = result.Seq, created = false });
    }

    [EndpointSummary("The whole board")]
    public static async Task<FileContentHttpResult> GetBoard(ISender sender, string id, CancellationToken cancellationToken) =>
        TypedResults.Bytes(await sender.Send(new GetBoardSnapshotQuery(id), cancellationToken), "application/json");

    [EndpointSummary("Changes after a sequence number")]
    [EndpointDescription("410 when the board no longer keeps them: take the whole board instead.")]
    public static async Task<FileContentHttpResult> GetChanges(ISender sender, string id, long after, CancellationToken cancellationToken) =>
        TypedResults.Bytes(await sender.Send(new GetBoardChangesQuery(id, after), cancellationToken), "application/json");

    [EndpointSummary("Submit one change")]
    public static async Task<Ok<SeqResponse>> SubmitChange(ISender sender, string id, HttpRequest request, CancellationToken cancellationToken)
    {
        var change = await Bodies.ReadJsonAsync(request, BoardLimits.MaxChangeBytes, cancellationToken);
        return TypedResults.Ok(new SeqResponse(await sender.Send(new SubmitChangeCommand(id, change), cancellationToken)));
    }

    [EndpointSummary("Delete a board")]
    [EndpointDescription("Only its owner can. Everyone on it is disconnected. With copies=remove the people it was shared with lose their copies too.")]
    public static async Task<NoContent> DeleteBoard(ISender sender, string id, string? copies, CancellationToken cancellationToken)
    {
        if (copies is not (null or "keep" or "remove")) throw new BadRequestException();
        await sender.Send(new DeleteBoardCommand(id, copies == "remove"), cancellationToken);
        return TypedResults.NoContent();
    }

    [EndpointSummary("Upload an image of a board")]
    [EndpointDescription("The path names the SHA-256 of the bytes; 400 with reason hash-mismatch when they differ.")]
    public static async Task<NoContent> UploadAsset(ISender sender, string id, string hash, HttpRequest request, CancellationToken cancellationToken)
    {
        var type = MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ? media.MediaType.Value : null;
        var content = await Bodies.ReadAsync(request, BoardLimits.MaxAssetBytes, cancellationToken);
        await sender.Send(new UploadAssetCommand(id, hash, type, content), cancellationToken);
        return TypedResults.NoContent();
    }

    [EndpointSummary("Download an image of a board")]
    public static async Task<FileStreamHttpResult> GetAsset(ISender sender, string id, string hash, HttpContext context, CancellationToken cancellationToken)
    {
        var asset = await sender.Send(new GetAssetQuery(id, hash), cancellationToken);
        var headers = context.Response.Headers;
        headers.CacheControl = "private, max-age=31536000, immutable";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return TypedResults.Stream(asset.Content, asset.ContentType);
    }

    /// <summary>Accepts the WebSocket and hands it to the live protocol.</summary>
    public static async Task Live(HttpContext context, string id, LiveSessions sessions)
    {
        if (!context.WebSockets.IsWebSocketRequest || !BoardLimits.IsBoardId(id))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await sessions.RunAsync(socket, id);
    }

    public sealed record SeqResponse(long Seq);
}

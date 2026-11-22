using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Penrill.Api.Hosting;
using Penrill.Core.Accounts;
using Penrill.Core.Boards;
using Penrill.Core.Rooms;
using Penrill.Core.Telemetry;

namespace Penrill.Api.Endpoints;

internal static partial class BoardEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards");
        boards.MapGet("/", async (ClaimsPrincipal user, IBoardStore store, CancellationToken cancel) =>
            Results.Ok(await store.List(user.UserName(), cancel)));
        boards.MapPut("/{id}", Create);
        boards.MapGet("/{id}", Snapshot);
        boards.MapGet("/{id}/changes", ChangesAfter);
        boards.MapPost("/{id}/changes", Submit);
        boards.MapDelete("/{id}", Delete);
    }

    /// <summary>Only the board's owner can delete it; devices still syncing it find it gone (404).</summary>
    private static async Task<IResult> Delete(
        string id, ClaimsPrincipal user, IBoardStore store, RoomRegistry rooms, ILoggerFactory loggers, CancellationToken cancel)
    {
        if (!BoardLimits.IsBoardId(id)) return Results.BadRequest();
        var name = user.UserName();
        switch (await store.Delete(id, name, cancel))
        {
            case BoardAccess.Missing: return Results.NotFound();
            case BoardAccess.Denied: return Results.Forbid();
        }
        await rooms.Forget(id);
        LogDeleted(loggers.CreateLogger("Penrill.Boards"), id, name);
        return Results.NoContent();
    }

    private static async Task<IResult> Create(
        string id, HttpRequest request, ClaimsPrincipal user, IBoardStore store, IBoardAccess access, RoomRegistry rooms,
        TimeProvider time, ILoggerFactory loggers, CancellationToken cancel)
    {
        if (!BoardLimits.IsBoardId(id)) return Results.BadRequest();
        if (!request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (await Bodies.Read(request, BoardLimits.MaxBoardBytes, cancel) is not { } body) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!BoardContent.TryParse(Bodies.ParseJson(body), out var content)) return Results.BadRequest();
        var owner = user.UserName();
        var guest = user.IsInRole(nameof(Role.Guest));
        if (!guest && await store.Create(new NewBoard(id, owner, content, time.GetUtcNow()), cancel))
        {
            LogCreated(loggers.CreateLogger("Penrill.Boards"), id, owner, content.Items.Count);
            return Results.Created($"/api/boards/{id}", new { seq = 0L, created = true });
        }
        if (await access.Check(owner, id, cancel) != BoardAccess.Allowed) return guest ? Results.Forbid() : Results.Conflict();
        return await BoardGuard.WithRoom(rooms, id, cancel, room => Task.FromResult(Results.Ok(new { seq = room.Seq, created = false })));
    }

    private static async Task<IResult> Snapshot(string id, ClaimsPrincipal user, IBoardAccess access, RoomRegistry rooms, CancellationToken cancel) =>
        await BoardGuard.Check(access, user.UserName(), id, cancel)
        ?? await BoardGuard.WithRoom(rooms, id, cancel, async room => Results.Bytes(await room.Snapshot(cancel), "application/json"));

    private static async Task<IResult> ChangesAfter(
        string id, long after, ClaimsPrincipal user, IBoardAccess access, RoomRegistry rooms, CancellationToken cancel) =>
        await BoardGuard.Check(access, user.UserName(), id, cancel)
        ?? await BoardGuard.WithRoom(rooms, id, cancel, async room =>
            await room.ChangesAfter(after, cancel) is { } changes
                ? Results.Bytes(changes, "application/json")
                : Results.StatusCode(StatusCodes.Status410Gone));

    private static async Task<IResult> Submit(
        string id, HttpRequest request, ClaimsPrincipal user, IBoardAccess access, RoomRegistry rooms, ILoggerFactory loggers, CancellationToken cancel)
    {
        var author = user.UserName();
        if (await BoardGuard.Check(access, author, id, cancel) is { } denied) return denied;
        if (await Bodies.Read(request, BoardLimits.MaxChangeBytes, cancel) is not { } body) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var node = Bodies.ParseJson(body);
        if (!ChangeCodec.TryParse(node, out var change))
        {
            ChangeRejected(loggers, ChangeCodec.IdOf(node), id, author);
            return Results.BadRequest(new { reason = ChangeCodec.Invalid });
        }
        return await BoardGuard.WithRoom(rooms, id, cancel, async room =>
        {
            var result = await room.Submit(change, JsonSerializer.SerializeToUtf8Bytes(node), author, cancel);
            return result.Status switch
            {
                SubmitStatus.Accepted or SubmitStatus.Duplicate => Results.Ok(new { seq = result.Seq }),
                SubmitStatus.Rejected => Results.BadRequest(new { reason = result.Reason }),
                _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            };
        });
    }

    internal static void ChangeRejected(ILoggerFactory loggers, string? change, string board, string user)
    {
        PenrillTelemetry.ChangesRejected.Add(1, PenrillTelemetry.Tag("reason", ChangeCodec.Invalid));
        LogInvalidChange(loggers.CreateLogger("Penrill.Boards"), change ?? "(no id)", board, user);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created board {Board} for {User} with {Items} items")]
    private static partial void LogCreated(ILogger logger, string board, string user, int items);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted board {Board} for {User}")]
    private static partial void LogDeleted(ILogger logger, string board, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected malformed change {Change} on board {Board} from {User}")]
    private static partial void LogInvalidChange(ILogger logger, string change, string board, string user);
}

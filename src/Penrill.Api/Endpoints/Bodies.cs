using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Penrill.Core.Boards;
using Penrill.Core.Rooms;

namespace Penrill.Api.Endpoints;

internal static class Bodies
{
    public static async Task<byte[]?> Read(HttpRequest request, int max, CancellationToken cancel)
    {
        if (request.ContentLength > max) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static JsonNode? ParseJson(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class BoardGuard
{
    public static async Task<IResult?> Check(IBoardAccess access, string user, string board, CancellationToken cancel)
    {
        if (!BoardLimits.IsBoardId(board)) return Results.BadRequest();
        return await access.Check(user, board, cancel) switch
        {
            BoardAccess.Allowed => null,
            BoardAccess.Denied => Results.Forbid(),
            _ => Results.NotFound()
        };
    }

    public static async Task<IResult> WithRoom(RoomRegistry rooms, string board, CancellationToken cancel, Func<BoardRoom, Task<IResult>> action)
    {
        using var lease = await rooms.Acquire(board, cancel);
        return lease is null ? Results.NotFound() : await action(lease.Room);
    }
}

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Penrill.Api.Endpoints;
using Penrill.Core.Accounts;
using Penrill.Core.Boards;
using Penrill.Core.Rooms;
using Penrill.Core.Telemetry;

namespace Penrill.Api.Live;

internal static partial class LiveEndpoint
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    public static void Map(RouteGroupBuilder api) => api.Map("/boards/{id}/live", Handle).AllowAnonymous();

    private static async Task Handle(
        HttpContext context, string id, AccountService accounts, IBoardAccess access, RoomRegistry rooms, LiveConnections live,
        TimeProvider time, ILoggerFactory loggers)
    {
        if (!context.WebSockets.IsWebSocketRequest || !BoardLimits.IsBoardId(id))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        Activity.Current = null;
        using var socket = new LiveSocket(webSocket);
        var logger = loggers.CreateLogger("Penrill.Live");
        var reason = LiveCloseReason.Normal;
        try
        {
            reason = await Serve(socket, id, accounts, access, rooms, live, time, loggers);
        }
        catch (LiveProtocolException error)
        {
            reason = LiveCloseReason.Protocol;
            LogProtocolError(logger, socket.User, id, error.Message);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            reason = socket.Reason;
        }
        socket.Close(reason);
        if (socket.Reason is not (LiveCloseReason.Normal or LiveCloseReason.SlowConsumer))
            PenrillTelemetry.LiveClosed.Add(1, PenrillTelemetry.Tag("reason", socket.Reason.ToString()));
        await socket.Shutdown();
    }

    private static async Task<LiveCloseReason> Serve(
        LiveSocket socket, string id, AccountService accounts, IBoardAccess access, RoomRegistry rooms, LiveConnections live,
        TimeProvider time, ILoggerFactory loggers)
    {
        using var helloTimeout = new CancellationTokenSource(HelloTimeout, time);
        using var timeoutRegistration = helloTimeout.Token.Register(() => socket.Close(LiveCloseReason.Protocol));
        var hello = await socket.Receive(16 * 1024);
        await timeoutRegistration.DisposeAsync();
        if (hello is null) return LiveCloseReason.Normal;
        if (Json(hello, "type") != "hello" || !IsProtocol(hello["protocol"]) || Json(hello, "token") is not { } token ||
            !TryAfter(hello["after"], out var after)) return LiveCloseReason.Protocol;

        var session = await accounts.Authenticate(token, socket.Stopping);
        if (session is null)
        {
            PenrillTelemetry.AuthFailures.Add(1, PenrillTelemetry.Tag("kind", "token"));
            return LiveCloseReason.Unauthorized;
        }
        switch (await access.Check(session.User, id, socket.Stopping))
        {
            case BoardAccess.Missing: return LiveCloseReason.NotFound;
            case BoardAccess.Denied: return LiveCloseReason.Forbidden;
        }
        using var lease = await rooms.Acquire(id, socket.Stopping);
        if (lease is null) return LiveCloseReason.NotFound;
        socket.User = session.User;
        using var registration = live.Register(session.TokenHash, socket);
        PenrillTelemetry.LiveConnections.Add(1);
        try
        {
            socket.TrySend(RoomFrames.Welcome(socket));
            await lease.Room.Attach(socket, after, socket.Stopping);
            var sending = socket.Send();
            using var expiry = time.CreateTimer(_ => socket.Close(LiveCloseReason.SessionEnded), null, session.Expires - time.GetUtcNow(), Timeout.InfiniteTimeSpan);
            try
            {
                return await Receive(socket, lease.Room, loggers);
            }
            finally
            {
                lease.Room.Detach(socket);
                socket.Close(LiveCloseReason.Normal);
                await sending;
            }
        }
        finally
        {
            PenrillTelemetry.LiveConnections.Add(-1);
        }
    }

    private static async Task<LiveCloseReason> Receive(LiveSocket socket, BoardRoom room, ILoggerFactory loggers)
    {
        while (await socket.Receive(BoardLimits.MaxChangeBytes + 1024) is { } message)
        {
            switch (Json(message, "type"))
            {
                case "submit":
                    var node = message["change"];
                    if (!ChangeCodec.TryParse(node, out var change))
                    {
                        BoardEndpoints.ChangeRejected(loggers, ChangeCodec.IdOf(node), room.BoardId, socket.User);
                        if (ChangeCodec.IdOf(node) is not { } changeId) return LiveCloseReason.Protocol;
                        socket.TrySend(RoomFrames.Reject(changeId, ChangeCodec.Invalid));
                        break;
                    }
                    var result = await room.Submit(change, JsonSerializer.SerializeToUtf8Bytes(node), socket.User, socket.Stopping);
                    switch (result.Status)
                    {
                        case SubmitStatus.Duplicate: socket.TrySend(RoomFrames.Ack(change.Id, result.Seq)); break;
                        case SubmitStatus.Rejected: socket.TrySend(RoomFrames.Reject(change.Id, result.Reason!)); break;
                        case SubmitStatus.Failed: return LiveCloseReason.ServerError;
                    }
                    break;
                case "signal":
                    if (message["data"] is not JsonObject data) return LiveCloseReason.Protocol;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
                    if (bytes.Length <= BoardLimits.MaxSignalBytes) room.Signal(socket, bytes);
                    break;
                case "ping":
                    socket.TrySend(RoomFrames.Pong);
                    break;
                default:
                    return LiveCloseReason.Protocol;
            }
        }
        return socket.Reason;
    }

    private static string? Json(JsonObject message, string key) =>
        message[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool IsProtocol(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int version) && version == BoardLimits.Protocol;

    private static bool TryAfter(JsonNode? node, out long? after)
    {
        after = null;
        if (node is null) return true;
        if (node is not JsonValue value || !value.TryGetValue(out long seq) || seq < 0) return false;
        after = seq;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed the live connection of {User} on board {Board}: {Problem}")]
    private static partial void LogProtocolError(ILogger logger, string user, string board, string problem);
}

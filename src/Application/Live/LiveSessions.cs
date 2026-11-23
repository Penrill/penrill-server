using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Penrill.Application.Accounts;
using Penrill.Application.Boards;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Telemetry;
using Penrill.Domain.Boards;

namespace Penrill.Application.Live;

/// <summary>
/// The live protocol (docs/PROTOCOL.md) over an accepted WebSocket: the hello frame authenticates and says how far
/// the client got, the room catches it up, then submits, signals and pings flow until either side closes.
/// Hosts only accept the socket and hand it here, so every host speaks the protocol the same way.
/// </summary>
public sealed partial class LiveSessions(
    IServiceScopeFactory scopes, RoomRegistry rooms, LiveConnections live, TimeProvider time, ILogger<LiveSessions> logger)
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private const int MaxQueuedSubmits = 64;

    public async Task RunAsync(WebSocket webSocket, string boardId)
    {
        Activity.Current = null;
        using var socket = new LiveSocket(webSocket);
        var reason = LiveCloseReason.Normal;
        try
        {
            reason = await Serve(socket, boardId);
        }
        catch (LiveProtocolException error)
        {
            reason = LiveCloseReason.Protocol;
            LogProtocolError(logger, socket.User, boardId, error.Message);
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

    private async Task<LiveCloseReason> Serve(LiveSocket socket, string boardId)
    {
        using var helloTimeout = new CancellationTokenSource(HelloTimeout, time);
        var timeoutRegistration = helloTimeout.Token.Register(() => socket.Close(LiveCloseReason.Protocol));
        var hello = await socket.Receive(16 * 1024);
        await timeoutRegistration.DisposeAsync();
        if (hello is null) return LiveCloseReason.Normal;
        if (Text(hello, "type") != "hello" || !IsProtocol(hello["protocol"]) || Text(hello, "token") is not { } token ||
            !TryAfter(hello["after"], out var after)) return LiveCloseReason.Protocol;

        AuthenticatedSession? session;
        BoardAccess access;
        using (var scope = scopes.CreateScope())
        {
            session = await scope.ServiceProvider.GetRequiredService<AccountService>().Authenticate(token, socket.Stopping);
            if (session is null)
            {
                PenrillTelemetry.AuthFailures.Add(1, PenrillTelemetry.Tag("kind", "token"));
                return LiveCloseReason.Unauthorized;
            }
            access = await scope.ServiceProvider.GetRequiredService<IApplicationDbContext>().AccessAsync(session.User, boardId, socket.Stopping);
        }
        switch (access)
        {
            case BoardAccess.Missing: return LiveCloseReason.NotFound;
            case BoardAccess.Denied: return LiveCloseReason.Forbidden;
        }
        using var lease = await rooms.Acquire(boardId, socket.Stopping);
        if (lease is null) return LiveCloseReason.NotFound;
        socket.User = session.User;
        using var registration = live.Register(session.CredentialId, socket);
        PenrillTelemetry.LiveConnections.Add(1);
        try
        {
            socket.TrySend(RoomFrames.Welcome(socket));
            await lease.Room.Attach(socket, after, socket.Stopping);
            var sending = socket.Send();
            using var expiry = session.Expires is { } expires
                ? time.CreateTimer(_ => socket.Close(LiveCloseReason.SessionEnded), null, Clamp(expires - time.GetUtcNow()), Timeout.InfiniteTimeSpan)
                : null;
            try
            {
                return await Receive(socket, lease.Room);
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

    private async Task<LiveCloseReason> Receive(LiveSocket socket, BoardRoom room)
    {
        // Submits wait for the store; signals (cursors, ink in flight) must not wait behind them, so submits go to a
        // queue of their own and are applied in order. A full queue stops reading, as a busy store always did.
        var submits = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(MaxQueuedSubmits) { SingleReader = true, SingleWriter = true });
        var applying = Apply(socket, room, submits.Reader);
        try
        {
            while (await socket.Receive(BoardLimits.MaxChangeBytes + 1024) is { } message)
            {
                switch (Text(message, "type"))
                {
                    case "submit":
                        await submits.Writer.WriteAsync(message, socket.Stopping);
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
        }
        finally
        {
            submits.Writer.TryComplete();
            await applying;
        }
        return socket.Reason;
    }

    /// <summary>Applies one socket's submits in the order they came; closes the socket on a broken message or store.</summary>
    private async Task Apply(LiveSocket socket, BoardRoom room, ChannelReader<JsonObject> submits)
    {
        try
        {
            await foreach (var message in submits.ReadAllAsync(socket.Stopping))
            {
                var node = message["change"];
                if (!ChangeCodec.TryParse(node, out var change))
                {
                    ChangeRejected(logger, ChangeCodec.IdOf(node), room.BoardId, socket.User);
                    if (ChangeCodec.IdOf(node) is not { } changeId)
                    {
                        socket.Close(LiveCloseReason.Protocol);
                        return;
                    }
                    socket.TrySend(RoomFrames.Reject(changeId, ChangeCodec.Invalid));
                    continue;
                }
                var result = await room.Submit(change, JsonSerializer.SerializeToUtf8Bytes(node), socket.User, socket.Stopping);
                switch (result.Status)
                {
                    case SubmitStatus.Duplicate: socket.TrySend(RoomFrames.Ack(change.Id, result.Seq)); break;
                    case SubmitStatus.Rejected: socket.TrySend(RoomFrames.Reject(change.Id, result.Reason!)); break;
                    case SubmitStatus.Failed:
                        socket.Close(LiveCloseReason.ServerError);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Counts and logs a change that does not parse, over HTTP or live.</summary>
    internal static void ChangeRejected(ILogger logger, string? change, string board, string user)
    {
        PenrillTelemetry.ChangesRejected.Add(1, PenrillTelemetry.Tag("reason", ChangeCodec.Invalid));
        LogInvalidChange(logger, change ?? "(no id)", board, user);
    }

    /// <summary>Timers cannot wait longer than about 49 days.</summary>
    private static TimeSpan Clamp(TimeSpan wait) => wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(40) ? TimeSpan.FromDays(40) : wait;

    private static string? Text(JsonObject message, string key) =>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected malformed change {Change} on board {Board} from {User}")]
    private static partial void LogInvalidChange(ILogger logger, string change, string board, string user);
}

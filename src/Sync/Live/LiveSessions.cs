using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sync.Protocol;

namespace Sync.Live;

/// <summary>
/// The live protocol (docs/PROTOCOL.md) over an accepted WebSocket: the hello frame authenticates,
/// the host policy grants a permission level and runtime limits, the room catches the client up,
/// then submits, signals and pings flow until either side closes.
/// </summary>
public sealed partial class LiveSessions(
    ILiveAuthenticator authenticator,
    ILiveAccessPolicy accessPolicy,
    ILiveEventObserver observer,
    RoomRegistry rooms,
    LiveConnections live,
    TimeProvider time,
    ILogger<LiveSessions> logger)
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
            SyncTelemetry.LiveClosed.Add(1, SyncTelemetry.Tag("reason", socket.Reason.ToString()));
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

        var identity = await authenticator.AuthenticateAsync(token, socket.Stopping);
        if (identity is null)
        {
            SyncTelemetry.AuthFailures.Add(1, SyncTelemetry.Tag("kind", "token"));
            return LiveCloseReason.Unauthorized;
        }

        var grant = await accessPolicy.EvaluateAsync(identity, boardId, socket.Stopping);
        switch (grant.Status)
        {
            case BoardAccessStatus.Missing: return LiveCloseReason.NotFound;
            case BoardAccessStatus.Denied: return LiveCloseReason.Forbidden;
        }

        var limits = grant.EffectiveLimits;
        using var lease = await rooms.Acquire(boardId, socket.Stopping);
        if (lease is null) return LiveCloseReason.NotFound;

        socket.UserId = identity.UserId;
        socket.User = identity.DisplayName;
        socket.Permission = grant.Permission;
        using var registration = live.Register(identity.CredentialId, socket);
        SyncTelemetry.LiveConnections.Add(1);
        var attached = false;
        var closeReason = LiveCloseReason.Normal;
        try
        {
            socket.TrySend(RoomFrames.Welcome(socket));
            attached = await lease.Room.Attach(socket, after, socket.Stopping, limits.MaxConcurrentPeers);
            if (!attached)
            {
                closeReason = LiveCloseReason.RoomFull;
                return closeReason;
            }
            await SafeNotify(() => observer.OnConnectedAsync(boardId, identity, grant.Permission, socket.Stopping));
            var sending = socket.Send();
            using var expiry = identity.ExpiresAt is { } expires
                ? time.CreateTimer(_ => socket.Close(LiveCloseReason.SessionEnded), null, Clamp(expires - time.GetUtcNow()), Timeout.InfiniteTimeSpan)
                : null;
            try
            {
                closeReason = await Receive(socket, lease.Room, identity, grant, limits);
                return closeReason;
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
            if (attached) await SafeNotify(() => observer.OnDisconnectedAsync(boardId, identity, closeReason, CancellationToken.None));
            SyncTelemetry.LiveConnections.Add(-1);
        }
    }

    private async Task<LiveCloseReason> Receive(LiveSocket socket, BoardRoom room, LiveIdentity identity, BoardAccessGrant grant, BoardLimitPolicy limits)
    {
        var submits = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(MaxQueuedSubmits) { SingleReader = true, SingleWriter = true });
        var applying = Apply(socket, room, identity, grant, limits, submits.Reader);
        try
        {
            while (await socket.Receive(limits.MaxChangeBytes + 1024) is { } message)
            {
                switch (Text(message, "type"))
                {
                    case "submit":
                        await submits.Writer.WriteAsync(message, socket.Stopping);
                        break;
                    case "signal":
                        if (message["data"] is not JsonObject data) return LiveCloseReason.Protocol;
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
                        if (bytes.Length <= limits.MaxSignalBytes) room.Signal(socket, bytes);
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

    private async Task Apply(
        LiveSocket socket,
        BoardRoom room,
        LiveIdentity identity,
        BoardAccessGrant grant,
        BoardLimitPolicy limits,
        ChannelReader<JsonObject> submits)
    {
        try
        {
            await foreach (var message in submits.ReadAllAsync(socket.Stopping))
            {
                var node = message["change"];
                if (!grant.CanEdit)
                {
                    var rejectedId = ChangeCodec.IdOf(node);
                    if (rejectedId is null)
                    {
                        socket.Close(LiveCloseReason.Protocol);
                        return;
                    }
                    SyncTelemetry.ChangesRejected.Add(1, SyncTelemetry.Tag("reason", ProtocolReasons.ReadOnly));
                    socket.TrySend(RoomFrames.Reject(rejectedId, ProtocolReasons.ReadOnly));
                    await SafeNotify(() => observer.OnChangeRejectedAsync(room.BoardId, identity, ProtocolReasons.ReadOnly, socket.Stopping));
                    continue;
                }
                if (!ChangeCodec.TryParse(node, out var change, limits))
                {
                    ChangeRejected(logger, ChangeCodec.IdOf(node), room.BoardId, socket.User);
                    await SafeNotify(() => observer.OnChangeRejectedAsync(room.BoardId, identity, ProtocolReasons.Invalid, socket.Stopping));
                    if (ChangeCodec.IdOf(node) is not { } changeId)
                    {
                        socket.Close(LiveCloseReason.Protocol);
                        return;
                    }
                    socket.TrySend(RoomFrames.Reject(changeId, ProtocolReasons.Invalid));
                    continue;
                }
                var body = JsonSerializer.SerializeToUtf8Bytes(node);
                var result = await room.Submit(change, body, socket.User, socket.Stopping, limits.MaxItems);
                switch (result.Status)
                {
                    case SubmitStatus.Accepted:
                        await SafeNotify(() => observer.OnChangeAcceptedAsync(room.BoardId, identity, result.Seq, body.Length, socket.Stopping));
                        break;
                    case SubmitStatus.Duplicate:
                        socket.TrySend(RoomFrames.Ack(change.Id, result.Seq));
                        break;
                    case SubmitStatus.Rejected:
                        socket.TrySend(RoomFrames.Reject(change.Id, result.Reason!));
                        await SafeNotify(() => observer.OnChangeRejectedAsync(room.BoardId, identity, result.Reason!, socket.Stopping));
                        break;
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

    private async Task SafeNotify(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogObserverFailed(logger, error);
        }
    }

    /// <summary>Counts and logs a change that does not parse, over HTTP or live.</summary>
    public static void ChangeRejected(ILogger logger, string? change, string board, string user)
    {
        SyncTelemetry.ChangesRejected.Add(1, SyncTelemetry.Tag("reason", ProtocolReasons.Invalid));
        LogInvalidChange(logger, change ?? "(no id)", board, user);
    }

    private static TimeSpan Clamp(TimeSpan wait) => wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(40) ? TimeSpan.FromDays(40) : wait;

    private static string? Text(JsonObject message, string key) =>
        message[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool IsProtocol(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int version) && version == ProtocolConstants.ProtocolVersion;

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Live event observer failed")]
    private static partial void LogObserverFailed(ILogger logger, Exception error);
}

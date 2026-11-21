using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Penrill.Core.Rooms;

namespace Penrill.Api.Live;

internal sealed class LiveProtocolException(string message) : Exception(message);

internal sealed class LiveSocket(WebSocket socket) : IRoomMember, IDisposable
{
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024) { SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly byte[] chunk = new byte[16 * 1024];
    private int closing;

    public Guid Id { get; } = Guid.NewGuid();
    public string User { get; set; } = "";
    public LiveCloseReason Reason { get; private set; } = LiveCloseReason.Normal;
    public CancellationToken Stopping => stop.Token;

    public bool TrySend(byte[] frame) => outgoing.Writer.TryWrite(frame);

    public void Close(LiveCloseReason reason)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        Reason = reason;
        stop.Cancel();
    }

    public async Task<JsonObject?> Receive(int max)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk.AsMemory(), stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new LiveProtocolException("Binary frames are not part of the protocol.");
            if (message.Length + result.Count > max) throw new LiveProtocolException("The message is too large.");
            message.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        try
        {
            return JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) as JsonObject
                ?? throw new LiveProtocolException("Messages are JSON objects.");
        }
        catch (JsonException)
        {
            throw new LiveProtocolException("The message is not JSON.");
        }
    }

    public async Task Send()
    {
        try
        {
            await foreach (var frame in outgoing.Reader.ReadAllAsync(stop.Token))
                await socket.SendAsync(frame, WebSocketMessageType.Text, true, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            Close(LiveCloseReason.Normal);
        }
    }

    public async Task Shutdown()
    {
        Close(LiveCloseReason.Normal);
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        var (status, description) = Reason switch
        {
            LiveCloseReason.SlowConsumer => ((WebSocketCloseStatus)4408, "too slow"),
            LiveCloseReason.Unauthorized or LiveCloseReason.SessionEnded => ((WebSocketCloseStatus)4401, "sign in"),
            LiveCloseReason.Forbidden => ((WebSocketCloseStatus)4403, "no access"),
            LiveCloseReason.NotFound => ((WebSocketCloseStatus)4404, "no such board"),
            LiveCloseReason.Protocol => ((WebSocketCloseStatus)4400, "protocol error"),
            LiveCloseReason.ServerError => (WebSocketCloseStatus.InternalServerError, "server error"),
            LiveCloseReason.ServerStopping => (WebSocketCloseStatus.EndpointUnavailable, "server stopping"),
            _ => (WebSocketCloseStatus.NormalClosure, "")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(status, description, timeout.Token);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
        }
    }

    public void Dispose() => stop.Dispose();
}

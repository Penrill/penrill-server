using System.Text.Json;
using Sync.Protocol;

namespace Sync.Live;

public static class RoomFrames
{
    public static readonly byte[] Pong = """{"type":"pong"}"""u8.ToArray();

    public static byte[] Welcome(IRoomMember member) => Write(writer =>
    {
        writer.WriteString("type", "welcome");
        writer.WriteString("client", member.Id);
        writer.WriteString("user", member.User);
    });

    public static byte[] Change(StoredChange change) => Write(writer =>
    {
        writer.WriteString("type", "change");
        WriteChangeFields(writer, change);
    });

    public static byte[] Changes(IEnumerable<StoredChange> changes) => Write(writer =>
    {
        writer.WriteString("type", "changes");
        WriteChangeList(writer, changes);
    });

    public static byte[] ChangeList(long seq, IEnumerable<StoredChange> changes) => Write(writer =>
    {
        writer.WriteNumber("seq", seq);
        WriteChangeList(writer, changes);
    });

    public static byte[] Snapshot(BoardDocument document) => Write(writer =>
    {
        writer.WriteString("type", "snapshot");
        writer.WritePropertyName("board");
        document.WriteSnapshot(writer);
    });

    public static byte[] Ready(long seq, IEnumerable<IRoomMember> peers) => Write(writer =>
    {
        writer.WriteString("type", "ready");
        writer.WriteNumber("seq", seq);
        writer.WriteStartArray("peers");
        foreach (var peer in peers)
        {
            writer.WriteStartObject();
            writer.WriteString("client", peer.Id);
            writer.WriteString("user", peer.User);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    public static byte[] Presence(string type, IRoomMember member) => Write(writer =>
    {
        writer.WriteString("type", type);
        writer.WriteString("client", member.Id);
        writer.WriteString("user", member.User);
    });

    public static byte[] Signal(IRoomMember from, ReadOnlySpan<byte> data)
    {
        using var buffer = new MemoryStream(data.Length + 128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "signal");
            writer.WriteString("client", from.Id);
            writer.WriteString("user", from.User);
            writer.WritePropertyName("data");
            writer.WriteRawValue(data, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static byte[] Ack(string id, long seq) => Write(writer =>
    {
        writer.WriteString("type", "ack");
        writer.WriteString("id", id);
        writer.WriteNumber("seq", seq);
    });

    public static byte[] Reject(string id, string reason) => Write(writer =>
    {
        writer.WriteString("type", "reject");
        writer.WriteString("id", id);
        writer.WriteString("reason", reason);
    });

    private static void WriteChangeList(Utf8JsonWriter writer, IEnumerable<StoredChange> changes)
    {
        writer.WriteStartArray("changes");
        foreach (var change in changes)
        {
            writer.WriteStartObject();
            WriteChangeFields(writer, change);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteChangeFields(Utf8JsonWriter writer, StoredChange change)
    {
        writer.WriteNumber("seq", change.Seq);
        writer.WriteString("author", change.Author);
        writer.WritePropertyName("change");
        writer.WriteRawValue(change.Body, skipInputValidation: true);
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

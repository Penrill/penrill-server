using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Penrill.Core.Boards;
using Penrill.Core.Rooms;
using Penrill.Storage.Sqlite;

namespace Penrill.Tests;

public sealed class RoomTests : IDisposable
{
    private const string Board = "0123456789abcdef0123456789abcdef";
    private readonly string directory = Directory.CreateTempSubdirectory("penrill-room-").FullName;
    private readonly IBoardStore store;

    public RoomTests()
    {
        var options = Options.Create(new SqliteStorageOptions { DataDirectory = directory });
        store = new SqliteBoardStore(new SqliteDatabase(options, NullLogger<SqliteDatabase>.Instance));
        var content = new BoardContent("Board", "dots", [new JsonObject { ["id"] = "seed", ["z"] = 1 }]);
        Assert.True(store.Create(new NewBoard(Board, "ann", content, DateTimeOffset.UtcNow), default).Result);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private RoomRegistry Registry(int snapshotEvery = 500, int recent = 1000) =>
        new(store, new RoomOptions { SnapshotEvery = snapshotEvery, RecentChanges = recent, IdleTimeout = TimeSpan.Zero }, TimeProvider.System, NullLoggerFactory.Instance);

    private static (Change Change, byte[] Body) Put(string changeId, string itemId)
    {
        var json = $$$"""{"id":"{{{changeId}}}","ops":[{"op":"put","item":{"id":"{{{itemId}}}","z":2}}]}""";
        Assert.True(ChangeCodec.TryParse(JsonNode.Parse(json), out var change));
        return (change, Encoding.UTF8.GetBytes(json));
    }

    private static async Task<SubmitResult> Submit(BoardRoom room, string changeId, string itemId)
    {
        var (change, body) = Put(changeId, itemId);
        return await room.Submit(change, body, "ann", default);
    }

    [Fact]
    public async Task OrdersChangesAndBroadcastsThemToEveryone()
    {
        var rooms = Registry();
        using var lease = (await rooms.Acquire(Board, default))!;
        var watcher = new RecordingMember();
        await lease.Room.Attach(watcher, after: 0, default);
        Assert.Equal(new SubmitResult(SubmitStatus.Accepted, 1), await Submit(lease.Room, "c1", "a"));
        Assert.Equal(new SubmitResult(SubmitStatus.Accepted, 2), await Submit(lease.Room, "c2", "b"));
        Assert.Equal(["ready", "change", "change"], watcher.Types);
        Assert.Equal(2, (long)watcher.Frames[^1]["seq"]!);
    }

    [Fact]
    public async Task AcknowledgesARepeatedChangeWithoutApplyingItTwice()
    {
        var rooms = Registry();
        using (var lease = (await rooms.Acquire(Board, default))!)
        {
            await Submit(lease.Room, "c1", "a");
            Assert.Equal(new SubmitResult(SubmitStatus.Duplicate, 1), await Submit(lease.Room, "c1", "a"));
        }
        await rooms.EvictIdle();
        using var reloaded = (await rooms.Acquire(Board, default))!;
        Assert.Equal(new SubmitResult(SubmitStatus.Duplicate, 1), await Submit(reloaded.Room, "c1", "a"));
        Assert.Equal(1, reloaded.Room.Seq);
    }

    [Fact]
    public async Task JoiningClientsCatchUpWithADeltaOrASnapshot()
    {
        var rooms = Registry(snapshotEvery: 2, recent: 2);
        using var lease = (await rooms.Acquire(Board, default))!;
        for (var i = 1; i <= 5; i++) await Submit(lease.Room, "c" + i, "item" + i);

        var current = new RecordingMember();
        await lease.Room.Attach(current, after: 5, default);
        Assert.Equal(["ready"], current.Types);

        var behind = new RecordingMember();
        await lease.Room.Attach(behind, after: 3, default);
        Assert.Equal(["changes", "ready"], behind.Types);
        Assert.Equal([4L, 5L], behind.Frames[0]["changes"]!.AsArray().Select(change => (long)change!["seq"]!));

        var lost = new RecordingMember();
        await lease.Room.Attach(lost, after: 1, default);
        Assert.Equal(["snapshot", "ready"], lost.Types);
        Assert.Equal(6, lost.Frames[0]["board"]!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task ABoardReloadsFromItsSnapshotAndLog()
    {
        var rooms = Registry(snapshotEvery: 2);
        byte[] before;
        using (var lease = (await rooms.Acquire(Board, default))!)
        {
            for (var i = 1; i <= 5; i++) await Submit(lease.Room, "c" + i, "item" + i);
            before = await lease.Room.Snapshot(default);
        }
        await rooms.EvictIdle();
        using var reloaded = (await rooms.Acquire(Board, default))!;
        Assert.Equal(before, await reloaded.Room.Snapshot(default));
    }

    [Fact]
    public async Task DisconnectsAMemberThatCannotKeepUp()
    {
        var rooms = Registry();
        using var lease = (await rooms.Acquire(Board, default))!;
        var slow = new RecordingMember { Accepts = 1 };
        await lease.Room.Attach(slow, after: 0, default);
        await Submit(lease.Room, "c1", "a");
        Assert.Equal(LiveCloseReason.SlowConsumer, slow.Closed);
    }

    private sealed class RecordingMember : IRoomMember
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string User => "ann";
        public int Accepts { get; init; } = int.MaxValue;
        public List<JsonObject> Frames { get; } = [];
        public LiveCloseReason? Closed { get; private set; }
        public List<string> Types => [.. Frames.Select(frame => (string)frame["type"]!)];

        public bool TrySend(byte[] frame)
        {
            if (Frames.Count >= Accepts) return false;
            Frames.Add(JsonNode.Parse(frame)!.AsObject());
            return true;
        }

        public void Close(LiveCloseReason reason) => Closed = reason;
    }
}

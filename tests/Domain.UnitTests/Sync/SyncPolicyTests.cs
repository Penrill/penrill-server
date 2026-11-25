using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sync.Live;
using Sync.Protocol;

namespace Domain.UnitTests.Sync;

public class SyncPolicyTests
{
    private const string Board = "0123456789abcdef0123456789abcdef";

    [Test]
    public void DynamicLimitPolicyCapsItemsAndOps()
    {
        var strict = new BoardLimitPolicy { MaxOpsPerChange = 1, MaxItems = 2 };
        var twoOps = JsonNode.Parse("""{"id":"c1","ops":[{"op":"put","item":{"id":"a"}},{"op":"put","item":{"id":"b"}}]}""");
        ChangeCodec.TryParse(twoOps, out _, strict).ShouldBeFalse();

        var oneOp = JsonNode.Parse("""{"id":"c1","ops":[{"op":"put","item":{"id":"a"}}]}""");
        ChangeCodec.TryParse(oneOp, out var change, strict).ShouldBeTrue();

        var doc = new BoardDocument(Board, 0, "T", "grid", [JsonNode.Parse("""{"id":"s1"}""")!.AsObject(), JsonNode.Parse("""{"id":"s2"}""")!.AsObject()]);
        doc.Fits(change!, strict.MaxItems).ShouldBeFalse();
        doc.Fits(change!, 10).ShouldBeTrue();
    }

    [Test]
    public async Task BoardRoomEnforcesPeerLimitHistorySinkAndRestore()
    {
        var store = new InMemoryBoardStore(Board);
        var sink = new RecordingHistorySink();
        var rooms = new RoomRegistry(store, sink, new InProcessRoomBackplane(), new RoomOptions { SnapshotEvery = 2 }, TimeProvider.System, NullLoggerFactory.Instance);

        using var lease = (await rooms.Acquire(Board, default))!;
        var first = new FakeMember("u1", "ann", BoardPermission.Edit);
        var second = new FakeMember("u2", "bob", BoardPermission.View);
        (await lease.Room.Attach(first, after: 0, default, maxConcurrentPeers: 1)).ShouldBeTrue();
        (await lease.Room.Attach(second, after: 0, default, maxConcurrentPeers: 1)).ShouldBeFalse();

        var c1Json = """{"id":"c1","ops":[{"op":"put","item":{"id":"a","z":1}}]}""";
        var c2Json = """{"id":"c2","ops":[{"op":"put","item":{"id":"b","z":2}}]}""";
        ChangeCodec.TryParse(JsonNode.Parse(c1Json), out var c1).ShouldBeTrue();
        ChangeCodec.TryParse(JsonNode.Parse(c2Json), out var c2).ShouldBeTrue();
        (await lease.Room.Submit(c1!, Encoding.UTF8.GetBytes(c1Json), "ann", default)).Status.ShouldBe(SubmitStatus.Accepted);
        (await lease.Room.Submit(c2!, Encoding.UTF8.GetBytes(c2Json), "ann", default)).Status.ShouldBe(SubmitStatus.Accepted);

        var restored = new BoardContent("Restored", "dots", [JsonNode.Parse("""{"id":"only"}""")!.AsObject()]);
        var restoreResult = await lease.Room.Restore(restored, "ann", default);
        restoreResult.Status.ShouldBe(SubmitStatus.Accepted);
        lease.Room.Title.ShouldBe("Restored");
        lease.Room.ItemCount.ShouldBe(1);

        await rooms.CloseAll();
        sink.Snapshots.ShouldNotBeEmpty();
    }

    private sealed class RecordingHistorySink : IBoardHistorySink
    {
        public List<SnapshotRecord> Snapshots { get; } = [];

        public Task OnSnapshotSavedAsync(SnapshotRecord snapshot, string? triggeredBy, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryBoardStore(string boardId) : IBoardStore
    {
        private string _title = "Board";
        private string _background = "grid";
        private long _seq;
        private long _snapshotSeq;
        private byte[] _snapshot = "[]"u8.ToArray();
        private readonly List<StoredChange> _changes = [];

        public Task<StoredBoard?> LoadAsync(string id, int recentChanges, CancellationToken cancellationToken) =>
            Task.FromResult<StoredBoard?>(id == boardId ? new StoredBoard(boardId, _title, _background, _seq, _snapshotSeq, _snapshot, _changes.ToList()) : null);

        public Task<AppendOutcome> AppendAsync(AppendRequest request, CancellationToken cancellationToken)
        {
            var existing = _changes.FirstOrDefault(c => c.Id == request.ChangeId);
            if (existing is not null) return Task.FromResult(new AppendOutcome(true, existing.Seq));
            _seq = request.Seq;
            _title = request.Title;
            _background = request.Background;
            _changes.Add(new StoredChange(request.Seq, request.ChangeId, request.Author, request.Body));
            return Task.FromResult(new AppendOutcome(false, request.Seq));
        }

        public Task SaveSnapshotAsync(SnapshotRecord snapshot, int retainBodies, TimeSpan retainReceipts, CancellationToken cancellationToken)
        {
            _snapshot = snapshot.Items;
            _snapshotSeq = snapshot.Seq;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMember(string userId, string user, BoardPermission permission) : IRoomMember
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string UserId => userId;
        public string User => user;
        public BoardPermission Permission => permission;
        public bool TrySend(byte[] frame) => true;
        public void Close(LiveCloseReason reason) { }
    }
}

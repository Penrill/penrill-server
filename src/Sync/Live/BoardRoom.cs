using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sync.Protocol;

namespace Sync.Live;

public enum SubmitStatus
{
    Accepted,
    Duplicate,
    Rejected,
    Failed
}

public readonly record struct SubmitResult(SubmitStatus Status, long Seq = 0, string? Reason = null);

public sealed partial class BoardRoom
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock membersLock = new();
    private readonly Dictionary<Guid, IRoomMember> members = [];
    private readonly BoardDocument document;
    private readonly RecentChanges recent;
    private readonly IBoardStore store;
    private readonly IBoardHistorySink historySink;
    private readonly IRoomBackplane backplane;
    private readonly RoomOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private long snapshotSeq;
    private string? lastAuthor;
    private Task snapshotting = Task.CompletedTask;

    private BoardRoom(
        BoardDocument document,
        RecentChanges recent,
        long snapshotSeq,
        IBoardStore store,
        IBoardHistorySink historySink,
        IRoomBackplane backplane,
        RoomOptions options,
        TimeProvider time,
        ILogger logger)
    {
        this.document = document;
        this.recent = recent;
        this.snapshotSeq = snapshotSeq;
        this.store = store;
        this.historySink = historySink;
        this.backplane = backplane;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    public string BoardId => document.Id;

    public long Seq => document.Seq;

    public string Title => document.Title;

    public string Background => document.Background;

    public int ItemCount => document.ItemCount;

    internal static async Task<BoardRoom?> Load(
        string boardId,
        IBoardStore store,
        IBoardHistorySink historySink,
        IRoomBackplane backplane,
        RoomOptions options,
        TimeProvider time,
        ILogger logger)
    {
        using var activity = SyncTelemetry.Source.StartActivity("board.load");
        activity?.SetTag("penrill.board", boardId);
        var started = time.GetTimestamp();
        var stored = await store.LoadAsync(boardId, options.RecentChanges, CancellationToken.None);
        if (stored is null) return null;
        var document = new BoardDocument(stored.Id, stored.SnapshotSeq, stored.Title, stored.Background, BoardDocument.ReadItems(stored.Items));
        var recent = new RecentChanges(options.RecentChanges, options.RecentBytes);
        var replayLimits = new BoardLimitPolicy { MaxOpsPerChange = 1_000_000, MaxItems = 1_000_000 };
        foreach (var change in stored.Recent)
        {
            if (change.Seq > document.Seq)
            {
                JsonNode? node = null;
                try { node = JsonNode.Parse(change.Body); }
                catch (JsonException) { }
                if (ChangeCodec.TryParse(node, out var parsed, replayLimits))
                {
                    document.Apply(change.Seq, parsed);
                }
                else
                {
                    logger.LogWarning("Skipping unparseable change at seq {Seq} while loading board {Board}", change.Seq, boardId);
                    document.AdvanceSeq(change.Seq);
                }
            }
            recent.Add(change);
        }
        if (document.Seq < stored.Seq)
            document.AdvanceSeq(stored.Seq);
        var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
        SyncTelemetry.LoadDuration.Record(elapsed);
        SyncTelemetry.ActiveRooms.Add(1);
        activity?.SetTag("penrill.board.items", document.ItemCount);
        LogLoaded(logger, boardId, document.Seq, document.ItemCount, elapsed);
        return new BoardRoom(document, recent, stored.SnapshotSeq, store, historySink, backplane, options, time, logger);
    }

    public async Task<SubmitResult> Submit(Change change, byte[] body, string author, CancellationToken cancel, int? maxItems = null)
    {
        using var activity = SyncTelemetry.Source.StartActivity("board.submit");
        activity?.SetTag("penrill.board", BoardId);
        activity?.SetTag("penrill.change.ops", change.Ops.Count);
        var started = time.GetTimestamp();
        await gate.WaitAsync(cancel);
        try
        {
            if (recent.TryFind(change.Id, out var known)) return Duplicate(known, activity);
            if (!document.Fits(change, maxItems)) return Reject(change, author, ProtocolReasons.BoardFull, activity);
            var seq = document.Seq + 1;
            var (title, background) = document.MetaAfter(change);
            AppendOutcome outcome;
            try
            {
                outcome = await store.AppendAsync(new AppendRequest(BoardId, seq, change.Id, author, body, title, background, time.GetUtcNow()), CancellationToken.None);
            }
            catch (Exception error)
            {
                LogAppendFailed(logger, error, BoardId, seq);
                activity?.SetStatus(ActivityStatusCode.Error, error.Message);
                return new SubmitResult(SubmitStatus.Failed);
            }
            if (outcome.Duplicate) return Duplicate(outcome.Seq, activity);
            document.Apply(seq, change);
            lastAuthor = author;
            var stored = new StoredChange(seq, change.Id, author, body);
            recent.Add(stored);
            var frame = RoomFrames.Change(stored);
            Broadcast(frame, except: null);
            backplane.Publish(BoardId, frame, exceptClientId: null);
            ScheduleSnapshot(author);
            SyncTelemetry.ChangesAccepted.Add(1);
            SyncTelemetry.SubmitDuration.Record(time.GetElapsedTime(started).TotalMilliseconds);
            activity?.SetTag("penrill.change.seq", seq);
            return new SubmitResult(SubmitStatus.Accepted, seq);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Replaces the room's content with a target snapshot by appending a single valid protocol change,
    /// so every connected editor receives the restored state live without reconnecting.
    /// </summary>
    public async Task<SubmitResult> Restore(BoardContent content, string author, CancellationToken cancel, int? maxItems = null)
    {
        await gate.WaitAsync(cancel);
        byte[] currentSnapshotBytes;
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) document.WriteSnapshot(writer);
            currentSnapshotBytes = buffer.ToArray();
        }
        finally
        {
            gate.Release();
        }

        var currentRoot = JsonNode.Parse(currentSnapshotBytes)!.AsObject();
        var currentItems = currentRoot["items"]!.AsArray();
        var targetIds = content.Items.Select(i => (string)i["id"]!).ToHashSet(StringComparer.Ordinal);
        var ops = new List<Operation>();
        var wireOps = new JsonArray();

        foreach (var entry in currentItems)
        {
            var id = (string)entry!["id"]!;
            if (!targetIds.Contains(id))
            {
                ops.Add(new DeleteItem(id));
                wireOps.Add(new JsonObject { ["op"] = "delete", ["id"] = id });
            }
        }
        foreach (var item in content.Items)
        {
            var id = (string)item["id"]!;
            var cloned = (JsonObject)item.DeepClone();
            ops.Add(new PutItem(id, cloned));
            wireOps.Add(new JsonObject { ["op"] = "put", ["item"] = (JsonObject)item.DeepClone() });
        }
        ops.Add(new SetMeta(content.Title, content.Background));
        wireOps.Add(new JsonObject { ["op"] = "meta", ["title"] = content.Title, ["background"] = content.Background });

        var changeId = "restore-" + Guid.NewGuid().ToString("N")[..24];
        var wire = new JsonObject { ["id"] = changeId, ["ops"] = wireOps };
        var body = JsonSerializer.SerializeToUtf8Bytes(wire);
        return await Submit(new Change(changeId, ops), body, author, cancel, maxItems);
    }

    public async Task<bool> Attach(IRoomMember member, long? after, CancellationToken cancel, int? maxConcurrentPeers = null)
    {
        await gate.WaitAsync(cancel);
        try
        {
            lock (membersLock)
            {
                if (maxConcurrentPeers is { } cap && members.Count >= cap && !members.ContainsKey(member.Id))
                    return false;
            }
            string kind;
            if (after == document.Seq) kind = "current";
            else if (after is { } known && known < document.Seq && recent.Covers(known))
            {
                member.TrySend(RoomFrames.Changes(recent.After(known)));
                kind = "delta";
            }
            else
            {
                member.TrySend(RoomFrames.Snapshot(document));
                kind = "snapshot";
            }
            SyncTelemetry.CatchUps.Add(1, SyncTelemetry.Tag("kind", kind));
            lock (membersLock)
            {
                if (maxConcurrentPeers is { } cap && members.Count >= cap && !members.ContainsKey(member.Id))
                    return false;
                member.TrySend(RoomFrames.Ready(document.Seq, members.Values));
                members[member.Id] = member;
            }
            var joinFrame = RoomFrames.Presence("join", member);
            Broadcast(joinFrame, member.Id);
            backplane.Publish(BoardId, joinFrame, member.Id);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Who is on the room right now.</summary>
    public IReadOnlyList<IRoomMember> Members()
    {
        lock (membersLock) return [.. members.Values];
    }

    public void Detach(IRoomMember member)
    {
        bool removed;
        lock (membersLock) removed = members.Remove(member.Id);
        if (removed)
        {
            var leaveFrame = RoomFrames.Presence("leave", member);
            Broadcast(leaveFrame, member.Id);
            backplane.Publish(BoardId, leaveFrame, member.Id);
        }
    }

    public void Signal(IRoomMember from, ReadOnlySpan<byte> data)
    {
        var frame = RoomFrames.Signal(from, data);
        Broadcast(frame, from.Id);
        backplane.Publish(BoardId, frame, from.Id);
    }

    public async Task<byte[]> Snapshot(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) document.WriteSnapshot(writer);
            return buffer.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SnapshotRecord> CaptureSnapshot(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            return new SnapshotRecord(BoardId, document.Seq, document.SerializeItems(), time.GetUtcNow());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<byte[]?> ChangesAfter(long after, CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            if (after == document.Seq) return RoomFrames.ChangeList(document.Seq, []);
            return after < document.Seq && after >= 0 && recent.Covers(after)
                ? RoomFrames.ChangeList(document.Seq, recent.After(after))
                : null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task Close(LiveCloseReason reason = LiveCloseReason.ServerStopping)
    {
        await gate.WaitAsync();
        try
        {
            await snapshotting;
            if (document.Seq > Interlocked.Read(ref snapshotSeq))
            {
                snapshotting = WriteSnapshot(new SnapshotRecord(BoardId, document.Seq, document.SerializeItems(), time.GetUtcNow()), lastAuthor);
                await snapshotting;
            }
        }
        finally
        {
            gate.Release();
        }
        IRoomMember[] remaining;
        lock (membersLock)
        {
            remaining = [.. members.Values];
            members.Clear();
        }
        foreach (var member in remaining) member.Close(reason);
        SyncTelemetry.ActiveRooms.Add(-1);
        LogClosed(logger, BoardId, document.Seq);
    }

    private SubmitResult Duplicate(long seq, Activity? activity)
    {
        SyncTelemetry.ChangesDuplicate.Add(1);
        activity?.SetTag("penrill.change.duplicate", true);
        return new SubmitResult(SubmitStatus.Duplicate, seq);
    }

    private SubmitResult Reject(Change change, string author, string reason, Activity? activity)
    {
        SyncTelemetry.ChangesRejected.Add(1, SyncTelemetry.Tag("reason", reason));
        activity?.SetTag("penrill.change.rejected", reason);
        LogRejected(logger, change.Id, BoardId, author, reason);
        return new SubmitResult(SubmitStatus.Rejected, Reason: reason);
    }

    private void ScheduleSnapshot(string? author)
    {
        if (document.Seq - Interlocked.Read(ref snapshotSeq) < options.SnapshotEvery || !snapshotting.IsCompleted) return;
        var record = new SnapshotRecord(BoardId, document.Seq, document.SerializeItems(), time.GetUtcNow());
        snapshotting = Task.Run(() => WriteSnapshot(record, author));
    }

    private async Task WriteSnapshot(SnapshotRecord record, string? author)
    {
        using var activity = SyncTelemetry.Source.StartActivity("board.snapshot");
        activity?.SetTag("penrill.board", BoardId);
        activity?.SetTag("penrill.change.seq", record.Seq);
        var started = time.GetTimestamp();
        try
        {
            await store.SaveSnapshotAsync(record, options.RecentChanges, options.ReceiptRetention, CancellationToken.None);
            Interlocked.Exchange(ref snapshotSeq, record.Seq);
            SyncTelemetry.SnapshotDuration.Record(time.GetElapsedTime(started).TotalMilliseconds);
            SyncTelemetry.SnapshotSize.Record(record.Items.Length);
            try
            {
                await historySink.OnSnapshotSavedAsync(record, author, CancellationToken.None);
            }
            catch (Exception error)
            {
                LogHistorySinkFailed(logger, error, BoardId, record.Seq);
            }
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            LogSnapshotFailed(logger, error, BoardId, record.Seq);
        }
    }

    private void Broadcast(byte[] frame, Guid? except)
    {
        List<IRoomMember>? slow = null;
        lock (membersLock)
        {
            foreach (var (id, member) in members)
                if (id != except && !member.TrySend(frame)) (slow ??= []).Add(member);
            if (slow is not null) foreach (var member in slow) members.Remove(member.Id);
        }
        if (slow is null) return;
        foreach (var member in slow)
        {
            LogSlowMember(logger, member.User, BoardId);
            member.Close(LiveCloseReason.SlowConsumer);
            Broadcast(RoomFrames.Presence("leave", member), member.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Loaded board {Board} at seq {Seq} with {Items} items in {Elapsed:F1} ms")]
    private static partial void LogLoaded(ILogger logger, string board, long seq, int items, double elapsed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Unloaded board {Board} at seq {Seq}")]
    private static partial void LogClosed(ILogger logger, string board, long seq);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected change {Change} on board {Board} from {User}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string change, string board, string user, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not store change {Seq} of board {Board}")]
    private static partial void LogAppendFailed(ILogger logger, Exception error, string board, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write the snapshot of board {Board} at seq {Seq}")]
    private static partial void LogSnapshotFailed(ILogger logger, Exception error, string board, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "History sink failed for board {Board} at seq {Seq}")]
    private static partial void LogHistorySinkFailed(ILogger logger, Exception error, string board, long seq);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected {User} from board {Board}: the connection could not keep up")]
    private static partial void LogSlowMember(ILogger logger, string user, string board);
}

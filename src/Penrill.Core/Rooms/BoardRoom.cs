using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Penrill.Core.Boards;
using Penrill.Core.Telemetry;

namespace Penrill.Core.Rooms;

public enum SubmitStatus { Accepted, Duplicate, Rejected, Failed }

public readonly record struct SubmitResult(SubmitStatus Status, long Seq = 0, string? Reason = null);

public sealed partial class BoardRoom
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock membersLock = new();
    private readonly Dictionary<Guid, IRoomMember> members = [];
    private readonly BoardDocument document;
    private readonly RecentChanges recent;
    private readonly IBoardStore store;
    private readonly RoomOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private long snapshotSeq;
    private Task snapshotting = Task.CompletedTask;

    private BoardRoom(BoardDocument document, RecentChanges recent, long snapshotSeq, IBoardStore store, RoomOptions options, TimeProvider time, ILogger logger)
    {
        this.document = document;
        this.recent = recent;
        this.snapshotSeq = snapshotSeq;
        this.store = store;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    public string BoardId => document.Id;

    public long Seq => document.Seq;

    internal static async Task<BoardRoom?> Load(string boardId, IBoardStore store, RoomOptions options, TimeProvider time, ILogger logger)
    {
        using var activity = PenrillTelemetry.Source.StartActivity("board.load");
        activity?.SetTag("penrill.board", boardId);
        var started = time.GetTimestamp();
        var stored = await store.Load(boardId, options.RecentChanges, CancellationToken.None);
        if (stored is null) return null;
        var document = new BoardDocument(stored.Id, stored.SnapshotSeq, stored.Title, stored.Background, BoardDocument.ReadItems(stored.Items));
        var recent = new RecentChanges(options.RecentChanges, options.RecentBytes);
        foreach (var change in stored.Recent)
        {
            if (change.Seq > document.Seq)
            {
                if (change.Seq != document.Seq + 1 || !ChangeCodec.TryParse(JsonNode.Parse(change.Body), out var parsed))
                    throw new InvalidDataException($"The log of board {boardId} is broken at seq {change.Seq}.");
                document.Apply(change.Seq, parsed);
            }
            recent.Add(change);
        }
        if (document.Seq != stored.Seq)
            throw new InvalidDataException($"The log of board {boardId} ends at seq {document.Seq}, expected {stored.Seq}.");
        var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
        PenrillTelemetry.LoadDuration.Record(elapsed);
        PenrillTelemetry.ActiveRooms.Add(1);
        activity?.SetTag("penrill.board.items", document.ItemCount);
        LogLoaded(logger, boardId, document.Seq, document.ItemCount, elapsed);
        return new BoardRoom(document, recent, stored.SnapshotSeq, store, options, time, logger);
    }

    public async Task<SubmitResult> Submit(Change change, byte[] body, string author, CancellationToken cancel)
    {
        using var activity = PenrillTelemetry.Source.StartActivity("board.submit");
        activity?.SetTag("penrill.board", BoardId);
        activity?.SetTag("penrill.change.ops", change.Ops.Count);
        var started = time.GetTimestamp();
        await gate.WaitAsync(cancel);
        try
        {
            if (recent.TryFind(change.Id, out var known)) return Duplicate(known, activity);
            if (!document.Fits(change)) return Reject(change, author, "board-full", activity);
            var seq = document.Seq + 1;
            var (title, background) = document.MetaAfter(change);
            AppendOutcome outcome;
            try
            {
                outcome = await store.Append(new AppendRequest(BoardId, seq, change.Id, author, body, title, background, time.GetUtcNow()), CancellationToken.None);
            }
            catch (Exception error)
            {
                LogAppendFailed(logger, error, BoardId, seq);
                activity?.SetStatus(ActivityStatusCode.Error, error.Message);
                return new SubmitResult(SubmitStatus.Failed);
            }
            if (outcome.Duplicate) return Duplicate(outcome.Seq, activity);
            document.Apply(seq, change);
            var stored = new StoredChange(seq, change.Id, author, body);
            recent.Add(stored);
            Broadcast(RoomFrames.Change(stored), except: null);
            ScheduleSnapshot();
            PenrillTelemetry.ChangesAccepted.Add(1);
            PenrillTelemetry.SubmitDuration.Record(time.GetElapsedTime(started).TotalMilliseconds);
            activity?.SetTag("penrill.change.seq", seq);
            return new SubmitResult(SubmitStatus.Accepted, seq);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Attach(IRoomMember member, long? after, CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
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
            PenrillTelemetry.CatchUps.Add(1, PenrillTelemetry.Tag("kind", kind));
            lock (membersLock)
            {
                member.TrySend(RoomFrames.Ready(document.Seq, members.Values));
                members[member.Id] = member;
            }
            Broadcast(RoomFrames.Presence("join", member), member.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Detach(IRoomMember member)
    {
        bool removed;
        lock (membersLock) removed = members.Remove(member.Id);
        if (removed) Broadcast(RoomFrames.Presence("leave", member), member.Id);
    }

    public void Signal(IRoomMember from, ReadOnlySpan<byte> data) => Broadcast(RoomFrames.Signal(from, data), from.Id);

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

    internal async Task Close()
    {
        await gate.WaitAsync();
        try
        {
            await snapshotting;
            if (document.Seq > Interlocked.Read(ref snapshotSeq))
            {
                snapshotting = WriteSnapshot(new SnapshotRecord(BoardId, document.Seq, document.SerializeItems(), time.GetUtcNow()));
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
        foreach (var member in remaining) member.Close(LiveCloseReason.ServerStopping);
        PenrillTelemetry.ActiveRooms.Add(-1);
        LogClosed(logger, BoardId, document.Seq);
    }

    private SubmitResult Duplicate(long seq, Activity? activity)
    {
        PenrillTelemetry.ChangesDuplicate.Add(1);
        activity?.SetTag("penrill.change.duplicate", true);
        return new SubmitResult(SubmitStatus.Duplicate, seq);
    }

    private SubmitResult Reject(Change change, string author, string reason, Activity? activity)
    {
        PenrillTelemetry.ChangesRejected.Add(1, PenrillTelemetry.Tag("reason", reason));
        activity?.SetTag("penrill.change.rejected", reason);
        LogRejected(logger, change.Id, BoardId, author, reason);
        return new SubmitResult(SubmitStatus.Rejected, Reason: reason);
    }

    private void ScheduleSnapshot()
    {
        if (document.Seq - Interlocked.Read(ref snapshotSeq) < options.SnapshotEvery || !snapshotting.IsCompleted) return;
        var record = new SnapshotRecord(BoardId, document.Seq, document.SerializeItems(), time.GetUtcNow());
        snapshotting = Task.Run(() => WriteSnapshot(record));
    }

    private async Task WriteSnapshot(SnapshotRecord record)
    {
        using var activity = PenrillTelemetry.Source.StartActivity("board.snapshot");
        activity?.SetTag("penrill.board", BoardId);
        activity?.SetTag("penrill.change.seq", record.Seq);
        var started = time.GetTimestamp();
        try
        {
            await store.SaveSnapshot(record, options.RecentChanges, options.ReceiptRetention, CancellationToken.None);
            Interlocked.Exchange(ref snapshotSeq, record.Seq);
            PenrillTelemetry.SnapshotDuration.Record(time.GetElapsedTime(started).TotalMilliseconds);
            PenrillTelemetry.SnapshotSize.Record(record.Items.Length);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected {User} from board {Board}: the connection could not keep up")]
    private static partial void LogSlowMember(ILogger logger, string user, string board);
}

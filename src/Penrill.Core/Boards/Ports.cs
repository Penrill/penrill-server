namespace Penrill.Core.Boards;

public enum BoardAccess { Missing, Denied, Allowed }

public sealed record BoardSummary(string Id, string Title, long Seq, DateTimeOffset Updated);

public sealed record StoredChange(long Seq, string Id, string Author, byte[] Body);

public sealed record StoredBoard(
    string Id, string Title, string Background, long Seq, long SnapshotSeq, byte[] Items, IReadOnlyList<StoredChange> Recent);

public sealed record AppendRequest(
    string Board, long Seq, string ChangeId, string Author, byte[] Body, string Title, string Background, DateTimeOffset At);

public readonly record struct AppendOutcome(bool Duplicate, long Seq);

public sealed record NewBoard(string Id, string Owner, BoardContent Content, DateTimeOffset At);

public sealed record SnapshotRecord(string Board, long Seq, byte[] Items, DateTimeOffset At);

public sealed record AssetInfo(string Hash, string ContentType, long Size);

public interface IBoardStore
{
    Task<StoredBoard?> Load(string board, int recent, CancellationToken cancel);
    Task<AppendOutcome> Append(AppendRequest request, CancellationToken cancel);
    Task SaveSnapshot(SnapshotRecord snapshot, int retainBodies, TimeSpan retainReceipts, CancellationToken cancel);
    Task<bool> Create(NewBoard board, CancellationToken cancel);
    Task<IReadOnlyList<BoardSummary>> List(string user, CancellationToken cancel);
    Task AddAsset(string board, AssetInfo asset, CancellationToken cancel);
    Task<AssetInfo?> FindAsset(string board, string hash, CancellationToken cancel);
}

public interface IBoardAccess
{
    Task<BoardAccess> Check(string user, string board, CancellationToken cancel);
}

public interface IAssetStore
{
    Task<bool> Exists(string hash, CancellationToken cancel);
    Task Save(string hash, ReadOnlyMemory<byte> content, CancellationToken cancel);
    Stream? OpenRead(string hash);
}

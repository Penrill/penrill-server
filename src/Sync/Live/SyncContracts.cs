using Sync.Protocol;

namespace Sync.Live;

public enum BoardAccessStatus
{
    Missing,
    Denied,
    Allowed
}

public sealed record StoredChange(long Seq, string Id, string Author, byte[] Body);

public sealed record StoredBoard(
    string Id,
    string Title,
    string Background,
    long Seq,
    long SnapshotSeq,
    byte[] Items,
    IReadOnlyList<StoredChange> Recent);

public sealed record AppendRequest(
    string Board,
    long Seq,
    string ChangeId,
    string Author,
    byte[] Body,
    string Title,
    string Background,
    DateTimeOffset At);

public readonly record struct AppendOutcome(bool Duplicate, long Seq);

public sealed record SnapshotRecord(string Board, long Seq, byte[] Items, DateTimeOffset At);

public sealed record LiveIdentity(
    string UserId,
    string DisplayName,
    string CredentialId,
    DateTimeOffset? ExpiresAt,
    bool IsGuest = false);

public sealed record BoardAccessGrant(
    BoardAccessStatus Status,
    BoardPermission Permission = BoardPermission.None,
    BoardLimitPolicy? Limits = null)
{
    public static readonly BoardAccessGrant Missing = new(BoardAccessStatus.Missing);

    public static readonly BoardAccessGrant Denied = new(BoardAccessStatus.Denied);

    public bool CanEdit => Status == BoardAccessStatus.Allowed && Permission >= BoardPermission.Edit;

    public BoardLimitPolicy EffectiveLimits => Limits ?? BoardLimitPolicy.SelfHostedDefault;
}

/// <summary>Persistent backing store for board rooms (snapshot + append-only change log).</summary>
public interface IBoardStore
{
    Task<StoredBoard?> LoadAsync(string boardId, int recentChanges, CancellationToken cancellationToken);

    Task<AppendOutcome> AppendAsync(AppendRequest request, CancellationToken cancellationToken);

    Task SaveSnapshotAsync(SnapshotRecord snapshot, int retainBodies, TimeSpan retainReceipts, CancellationToken cancellationToken);
}

/// <summary>Optional hook called whenever a board room persists a snapshot (for example, version history archival).</summary>
public interface IBoardHistorySink
{
    Task OnSnapshotSavedAsync(SnapshotRecord snapshot, string? triggeredBy, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Authenticates the opaque bearer token sent in a WebSocket hello frame.</summary>
public interface ILiveAuthenticator
{
    Task<LiveIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Evaluates whether an authenticated identity may open or edit a board, and under which limits.</summary>
public interface ILiveAccessPolicy
{
    Task<BoardAccessGrant> EvaluateAsync(LiveIdentity identity, string boardId, CancellationToken cancellationToken);
}

/// <summary>Optional observer for live WebSocket lifecycle and change events (audit logging, metering).</summary>
public interface ILiveEventObserver
{
    Task OnConnectedAsync(string boardId, LiveIdentity identity, BoardPermission permission, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnDisconnectedAsync(string boardId, LiveIdentity identity, LiveCloseReason reason, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnChangeAcceptedAsync(string boardId, LiveIdentity identity, long seq, int byteSize, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnChangeRejectedAsync(string boardId, LiveIdentity identity, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Optional hook for distributing frames across multiple server instances.</summary>
public interface IRoomBackplane
{
    void Publish(string boardId, byte[] frame, Guid? exceptClientId);
}

internal sealed class NullBoardHistorySink : IBoardHistorySink;

internal sealed class NullLiveEventObserver : ILiveEventObserver;

internal sealed class InProcessRoomBackplane : IRoomBackplane
{
    public void Publish(string boardId, byte[] frame, Guid? exceptClientId) { }
}

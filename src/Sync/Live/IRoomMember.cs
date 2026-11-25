using Sync.Protocol;

namespace Sync.Live;

public enum LiveCloseReason
{
    Normal,
    SlowConsumer,
    Unauthorized,
    Forbidden,
    NotFound,
    Protocol,
    ServerError,
    ServerStopping,
    SessionEnded,
    RoomFull
}

public interface IRoomMember
{
    Guid Id { get; }

    /// <summary>Stable account identifier (username on self-hosted server, UUID string on cloud).</summary>
    string UserId { get; }

    /// <summary>Display name or handle sent to peers in presence and welcome frames.</summary>
    string User { get; }

    BoardPermission Permission { get; }

    bool TrySend(byte[] frame);

    void Close(LiveCloseReason reason);
}

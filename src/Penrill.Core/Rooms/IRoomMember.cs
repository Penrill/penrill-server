namespace Penrill.Core.Rooms;

public enum LiveCloseReason { Normal, SlowConsumer, Unauthorized, Forbidden, NotFound, Protocol, ServerError, ServerStopping, SessionEnded }

public interface IRoomMember
{
    Guid Id { get; }
    string User { get; }
    bool TrySend(byte[] frame);
    void Close(LiveCloseReason reason);
}

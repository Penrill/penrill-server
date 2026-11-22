namespace Penrill.Domain.Events;

/// <summary>A board is gone: everyone on it is disconnected.</summary>
public sealed class BoardDeletedEvent(string boardId) : BaseEvent
{
    public string BoardId { get; } = boardId;
}

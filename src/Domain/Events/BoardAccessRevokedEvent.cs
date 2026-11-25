namespace Domain.Events;

/// <summary>Someone may have lost access to a board (taken off it, or its draw link turned off): open connections are checked again.</summary>
public sealed class BoardAccessRevokedEvent(string boardId) : BaseEvent
{
    public string BoardId { get; } = boardId;
}

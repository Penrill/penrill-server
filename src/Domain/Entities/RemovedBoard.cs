namespace Penrill.Domain.Entities;

/// <summary>
/// A board its owner deleted and asked to take off the devices of everyone it was shared with. Those devices
/// learn it when they next look for the board.
/// </summary>
public class RemovedBoard
{
    public string Id { get; set; } = "";

    public DateTimeOffset At { get; set; }
}

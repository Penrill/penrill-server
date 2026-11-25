namespace Domain.Entities;

/// <summary>
/// One accepted change in a board's log. <see cref="Body"/> is cleared once a later snapshot covers it;
/// the row itself stays a while longer as a receipt, so a client resending the change gets its sequence back.
/// </summary>
public class BoardChange
{
    public string BoardId { get; set; } = "";

    public long Seq { get; set; }

    /// <summary>The id the client gave the change; unique per board.</summary>
    public string ChangeId { get; set; } = "";

    public string Author { get; set; } = "";

    public DateTimeOffset At { get; set; }

    public byte[]? Body { get; set; }
}

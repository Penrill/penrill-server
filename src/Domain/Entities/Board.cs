namespace Penrill.Domain.Entities;

/// <summary>
/// A board: its latest metadata, the sequence number of its last change and a snapshot of its items at
/// <see cref="SnapshotSeq"/>. The items after the snapshot are replayed from <see cref="BoardChange"/> rows.
/// </summary>
public class Board : BaseEntity
{
    public string Id { get; set; } = "";

    /// <summary>Who made the board. Null once that account is gone; an administrator can give it a new owner.</summary>
    public string? Owner { get; set; }

    public string Title { get; set; } = "";

    public string Background { get; set; } = "";

    public long Seq { get; set; }

    public long SnapshotSeq { get; set; }

    /// <summary>The items at <see cref="SnapshotSeq"/>, as a JSON array.</summary>
    public byte[] Snapshot { get; set; } = [];

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset Updated { get; set; }
}

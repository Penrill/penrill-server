namespace Domain.Entities;

/// <summary>An image a board uses. The bytes live in the asset store under <see cref="Hash"/>, shared by every board that has them.</summary>
public class BoardAsset
{
    public string BoardId { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    public string Hash { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Size { get; set; }
}

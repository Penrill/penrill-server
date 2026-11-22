namespace Penrill.Domain.Entities;

/// <summary>A one-time link (see <see cref="GrantKind"/>). Only the SHA-256 of its token is stored.</summary>
public class Grant
{
    public string Id { get; set; } = "";

    public string TokenHash { get; set; } = "";

    public GrantKind Kind { get; set; }

    /// <summary>The role an invite gives, the user a reset is for, empty for a claim.</summary>
    public string Subject { get; set; } = "";

    public string CreatedBy { get; set; } = "";

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset? Expires { get; set; }

    public int UsesLeft { get; set; }

    public bool IsValidAt(DateTimeOffset now) => UsesLeft > 0 && (Expires is null || Expires > now);
}

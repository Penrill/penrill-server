namespace Penrill.Domain.Entities;

/// <summary>
/// A bearer token an editor signs in with. Only the SHA-256 of the token is stored.
/// Sessions expire; device tokens last until revoked.
/// </summary>
public class Credential : BaseEntity
{
    /// <summary>Short public id, used to list and revoke devices without revealing the token.</summary>
    public string Id { get; set; } = "";

    public string TokenHash { get; set; } = "";

    public string UserId { get; set; } = "";

    public CredentialKind Kind { get; set; }

    public string Device { get; set; } = "";

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset? LastUsed { get; set; }

    public DateTimeOffset? Expires { get; set; }

    public bool IsValidAt(DateTimeOffset now) => Expires is null || Expires > now;
}

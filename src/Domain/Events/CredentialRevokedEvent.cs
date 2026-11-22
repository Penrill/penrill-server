namespace Penrill.Domain.Events;

/// <summary>A credential stopped being valid (signed out, revoked, its account disabled): connections using it must close.</summary>
public sealed class CredentialRevokedEvent(string credentialId) : BaseEvent
{
    public string CredentialId { get; } = credentialId;
}

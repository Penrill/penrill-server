namespace Penrill.Domain.Enums;

/// <summary>A session comes from a password sign-in and expires; a device token comes from an approved link and lives until revoked.</summary>
public enum CredentialKind { Session = 0, Device = 1 }

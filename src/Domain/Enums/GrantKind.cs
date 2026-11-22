namespace Penrill.Domain.Enums;

/// <summary>
/// A one-time link that lets someone do one thing without an account or without their password:
/// claim a fresh server, join it by invite, or set a new password.
/// </summary>
public enum GrantKind { Claim = 0, Invite = 1, Reset = 2 }

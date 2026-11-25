namespace Sync.Protocol;

/// <summary>Additive host capabilities returned by GET /api/server so editors can enable host-specific UI.</summary>
public sealed record ServerCapabilities(
    string? CloudApi = null,
    bool Workspaces = false,
    IReadOnlyList<string>? Permissions = null,
    bool VersionHistory = false,
    bool Billing = false,
    bool Relay = false);

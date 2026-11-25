namespace Sync.Protocol;

public static class ProtocolConstants
{
    /// <summary>The live protocol version editors send in their hello frame.</summary>
    public const int ProtocolVersion = 1;
    public const int MaxTitle = 256;
    public const int MaxBackground = 64;
    public const int MaxItemId = 128;
    public const int MaxChangeId = 64;
    /// <summary>Folder, member, person and template names.</summary>
    public const int MaxLabel = 128;

    public static bool IsBoardId(string? id) => id is { Length: 32 or 64 } && id.All(char.IsAsciiHexDigitLower);

    public static bool IsAssetHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitLower);

    public static bool IsLabel(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxLabel;
}

public static class ProtocolReasons
{
    public const string Invalid = "invalid";
    public const string HashMismatch = "hash-mismatch";
    public const string BoardFull = "board-full";
    public const string Removed = "removed";
    public const string ReadOnly = "read-only";
    public const string QuotaExceeded = "quota-exceeded";
    public const string RoomFull = "room-full";
    public const string PlanRequired = "plan-required";
}

namespace Penrill.Domain.Constants;

public static class BoardLimits
{
    /// <summary>The live protocol version editors send in their hello frame.</summary>
    public const int Protocol = 1;
    public const int MaxOps = 10_000;
    public const int MaxChangeBytes = 8 * 1024 * 1024;
    public const int MaxBoardBytes = 32 * 1024 * 1024;
    public const int MaxItems = 200_000;
    public const int MaxTitle = 256;
    public const int MaxBackground = 64;
    public const int MaxItemId = 128;
    public const int MaxChangeId = 64;
    public const int MaxSignalBytes = 64 * 1024;
    public const int MaxAssetBytes = 25 * 1024 * 1024;
    /// <summary>All images on one board together, so a draw link cannot be used to fill the disk.</summary>
    public const long MaxBoardAssetBytes = 256L * 1024 * 1024;
    /// <summary>Folder, member, person and template names.</summary>
    public const int MaxLabel = 128;

    public static bool IsBoardId(string? id) => id is { Length: 32 or 64 } && id.All(char.IsAsciiHexDigitLower);

    public static bool IsAssetHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitLower);

    public static bool IsLabel(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxLabel;
}

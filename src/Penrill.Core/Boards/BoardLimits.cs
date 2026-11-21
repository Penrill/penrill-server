namespace Penrill.Core.Boards;

public static class BoardLimits
{
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

    public static bool IsBoardId(string id) => id.Length is 32 or 64 && id.All(char.IsAsciiHexDigitLower);

    public static bool IsAssetHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower);
}

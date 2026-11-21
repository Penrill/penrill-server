using System.Text.Json;

namespace Penrill.Core.Sharing;

public sealed record FolderInfo(string Id, string Name, string Owner);

public sealed record MemberInfo(string Folder, string Name);

public sealed record FolderBoard(string Folder, string Id, string Title, long Seq, DateTimeOffset Updated);

public sealed record TemplateInfo(string Id, string Name, string Folder, string Author);

public sealed record SharingCatalog(
    IReadOnlyList<FolderInfo> Folders, IReadOnlyList<MemberInfo> Members, IReadOnlyList<FolderBoard> Boards, IReadOnlyList<TemplateInfo> Templates);

public interface ISharingStore
{
    Task<SharingCatalog> Catalog(string user, CancellationToken cancel);
    Task CreateFolder(string owner, string name, CancellationToken cancel);
    Task<bool> AddMember(string owner, string folder, string member, CancellationToken cancel);
    Task<bool> ShareBoard(string user, string folder, string board, CancellationToken cancel);
}

public interface ITemplateStore
{
    Task<bool> Publish(string author, string folder, string name, byte[] data, CancellationToken cancel);
    Task<byte[]?> Find(string user, string id, CancellationToken cancel);
}

public static class TemplateFile
{
    public const int MaxLabel = 128;

    public static bool IsLabel(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxLabel;

    public static bool IsBoard(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            using var json = JsonDocument.ParseValue(ref reader);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.ValueEquals("inkboard") &&
                root.TryGetProperty("version", out var version) && version.TryGetInt32(out var number) && number >= 1 &&
                root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

using System.Text.Json;

namespace Penrill.Core.Sharing;

public sealed record FolderInfo(string Id, string Name, string Owner);

public sealed record MemberInfo(string Folder, string Name);

public sealed record FolderBoard(string Folder, string Id, string Title, long Seq, DateTimeOffset Updated);

/// <summary>A shared template. <c>Updated</c> (Unix milliseconds) changes whenever its author edits it, so copies can follow.</summary>
public sealed record TemplateInfo(string Id, string Name, string Folder, string Author, long Updated);

/// <summary>A template as the admin pages list it: with the folder's name instead of its id.</summary>
public sealed record SharedTemplate(string Id, string Name, string Author, string? FolderName, DateTimeOffset Updated);

public sealed record SharingCatalog(
    IReadOnlyList<FolderInfo> Folders, IReadOnlyList<MemberInfo> Members, IReadOnlyList<FolderBoard> Boards, IReadOnlyList<TemplateInfo> Templates);

public interface ISharingStore
{
    Task<SharingCatalog> Catalog(string user, CancellationToken cancel);
    Task CreateFolder(string owner, string name, CancellationToken cancel);
    Task<bool> AddMember(string owner, string folder, string member, CancellationToken cancel);
    Task<bool> ShareBoard(string user, string folder, string board, CancellationToken cancel);
    /// <summary>People the board's owner let in by name. Null when the board isn't the caller's.</summary>
    Task<IReadOnlyList<string>?> People(string owner, string board, CancellationToken cancel);
    /// <summary>Lets a person draw on the owner's board. False when the board isn't the owner's or nobody has that name.</summary>
    Task<bool> AddPerson(string owner, string board, string name, CancellationToken cancel);
    /// <summary>
    /// The board's draw link, made now when <paramref name="create"/> is set. Null when the board isn't
    /// the caller's; a state without a token while the link is off.
    /// </summary>
    Task<BoardLinkState?> BoardLink(string owner, string board, bool create, CancellationToken cancel);
    /// <summary>Turns the draw link off and takes out everyone who came in through it.</summary>
    Task<bool> RevokeBoardLink(string owner, string board, CancellationToken cancel);
    /// <summary>The board a draw link opens; null when the link is unknown or turned off.</summary>
    Task<string?> LinkedBoard(string token, CancellationToken cancel);
    /// <summary>Lets a guest account draw on the board its link opens.</summary>
    Task AddGuest(string token, string user, CancellationToken cancel);
    Task<bool> RemovePerson(string owner, string board, string name, CancellationToken cancel);
}

public interface ITemplateStore
{
    /// <summary>Shares a template with everyone (<paramref name="folder"/> empty) or one folder the author is in. Null when the folder isn't theirs.</summary>
    Task<TemplateInfo?> Publish(string author, string folder, string name, byte[] data, CancellationToken cancel);
    /// <summary>The author edits a shared template in place: everyone who added it gets the new version.</summary>
    Task<TemplateInfo?> Update(string author, string id, string name, byte[] data, CancellationToken cancel);
    /// <summary>Stops sharing a template: its author may, and so may an admin.</summary>
    Task<bool> Remove(string user, bool admin, string id, CancellationToken cancel);
    Task<byte[]?> Find(string user, string id, CancellationToken cancel);
    Task<IReadOnlyList<SharedTemplate>> All(CancellationToken cancel);
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

/// <summary>A board's draw link: its token, or null while the link is off.</summary>
public sealed record BoardLinkState(string? Token);

using Penrill.Core.Boards;
using Penrill.Core.Sharing;

namespace Penrill.Storage.Sqlite;

internal sealed class SqliteSharingStore(SqliteDatabase database) : ISharingStore, IBoardAccess, ITemplateStore
{
    private const string FoldersOf = "SELECT folder FROM members WHERE user=$user";

    public Task<BoardAccess> Check(string user, string board, CancellationToken cancel)
    {
        using var db = database.Open();
        var rows = db.Rows($"SELECT id IN ({SqlAccess.BoardsOf}) FROM boards WHERE id=$board",
            row => row.GetBoolean(0), ("board", board), ("user", user));
        return Task.FromResult(rows.Count == 0 ? BoardAccess.Missing : rows[0] ? BoardAccess.Allowed : BoardAccess.Denied);
    }

    public Task<SharingCatalog> Catalog(string user, CancellationToken cancel)
    {
        using var db = database.Open();
        var me = ("user", (object?)user);
        var folders = db.Rows("SELECT id, name, owner FROM folders JOIN members ON members.folder=folders.id WHERE members.user=$user ORDER BY name",
            row => new FolderInfo(row.GetString(0), row.GetString(1), row.GetString(2)), me);
        var members = db.Rows($"SELECT folder, user FROM members WHERE folder IN ({FoldersOf}) ORDER BY user",
            row => new MemberInfo(row.GetString(0), row.GetString(1)), me);
        var boards = db.Rows($"""
            SELECT folder_boards.folder, boards.id, boards.title, boards.seq, boards.updated
            FROM folder_boards JOIN boards ON folder_boards.board=boards.id
            WHERE folder_boards.folder IN ({FoldersOf}) ORDER BY boards.title
            """,
            row => new FolderBoard(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), SqliteCommands.ParseStamp(row.GetString(4))), me);
        var templates = db.Rows($"SELECT id, name, folder, author FROM templates WHERE folder='' OR folder IN ({FoldersOf}) ORDER BY name",
            row => new TemplateInfo(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)), me);
        return Task.FromResult(new SharingCatalog(folders, members, boards, templates));
    }

    public Task CreateFolder(string owner, string name, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var id = Guid.NewGuid().ToString("N");
        db.Execute("INSERT INTO folders(id, name, owner) VALUES($id, $name, $user); INSERT INTO members(folder, user) VALUES($id, $user)",
            ("id", id), ("name", name.Trim()), ("user", owner));
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<bool> AddMember(string owner, string folder, string member, CancellationToken cancel)
    {
        using var db = database.Open();
        var added = db.Execute("""
            INSERT OR IGNORE INTO members(folder, user)
            SELECT folders.id, accounts.name FROM folders, accounts WHERE folders.id=$folder AND folders.owner=$user AND accounts.name=$name
            """, ("folder", folder), ("user", owner), ("name", member.Trim().ToLowerInvariant()));
        return Task.FromResult(added == 1);
    }

    public Task<bool> ShareBoard(string user, string folder, string board, CancellationToken cancel)
    {
        using var db = database.Open();
        var shared = db.Execute("""
            INSERT OR IGNORE INTO folder_boards(folder, board)
            SELECT members.folder, owners.board FROM members, owners
            WHERE members.folder=$folder AND owners.board=$board AND members.user=$user AND owners.user=$user
            """, ("folder", folder), ("board", board), ("user", user));
        return Task.FromResult(shared == 1);
    }

    public Task<bool> Publish(string author, string folder, string name, byte[] data, CancellationToken cancel)
    {
        using var db = database.Open();
        var published = db.Execute($"""
            INSERT INTO templates(id, folder, author, name, data)
            SELECT $id, $folder, $user, $name, $data WHERE $folder='' OR $folder IN ({FoldersOf})
            """, ("id", Guid.NewGuid().ToString("N")), ("folder", folder), ("user", author), ("name", name.Trim()), ("data", data));
        return Task.FromResult(published == 1);
    }

    public Task<byte[]?> Find(string user, string id, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Scalar($"SELECT data FROM templates WHERE id=$id AND (folder='' OR folder IN ({FoldersOf}))",
            ("id", id), ("user", user)) as byte[]);
    }
}

using Microsoft.Data.Sqlite;
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
        var templates = db.Rows($"SELECT {TemplateColumns} FROM templates WHERE folder='' OR folder IN ({FoldersOf}) ORDER BY name",
            ReadTemplate, me);
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

    public Task<IReadOnlyList<string>?> People(string owner, string board, CancellationToken cancel)
    {
        using var db = database.Open();
        if (db.Scalar("SELECT 1 FROM owners WHERE board=$board AND user=$user", ("board", board), ("user", owner)) is null)
            return Task.FromResult<IReadOnlyList<string>?>(null);
        return Task.FromResult<IReadOnlyList<string>?>(db.Rows("SELECT user FROM board_people WHERE board=$board ORDER BY user", row => row.GetString(0), ("board", board)));
    }

    public Task<bool> AddPerson(string owner, string board, string name, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("""
            INSERT OR IGNORE INTO board_people(board, user)
            SELECT owners.board, accounts.name FROM owners, accounts
            WHERE owners.board=$board AND owners.user=$user AND accounts.name=$name AND accounts.name<>$user
            """, ("board", board), ("user", owner), ("name", name.Trim().ToLowerInvariant()));
        return Task.FromResult(db.Scalar("SELECT 1 FROM board_people WHERE board=$board AND user=$name",
            ("board", board), ("name", name.Trim().ToLowerInvariant())) is not null);
    }

    public Task<bool> RemovePerson(string owner, string board, string name, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("""
            DELETE FROM board_people WHERE board=$board AND user=$name AND $board IN (SELECT board FROM owners WHERE user=$user)
            """, ("board", board), ("user", owner), ("name", name.Trim().ToLowerInvariant())) == 1);
    }

    public Task<BoardLinkState?> BoardLink(string owner, string board, bool create, CancellationToken cancel)
    {
        using var db = database.Open();
        if (db.Scalar("SELECT 1 FROM owners WHERE board=$board AND user=$user", ("board", board), ("user", owner)) is null)
            return Task.FromResult<BoardLinkState?>(null);
        if (create) db.Execute("INSERT OR IGNORE INTO board_links(board, token) VALUES($board, $token)",
            ("board", board), ("token", Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))));
        return Task.FromResult<BoardLinkState?>(new(db.Scalar("SELECT token FROM board_links WHERE board=$board", ("board", board)) as string));
    }

    public Task<bool> RevokeBoardLink(string owner, string board, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        if (db.Scalar("SELECT 1 FROM owners WHERE board=$board AND user=$user", ("board", board), ("user", owner)) is null)
            return Task.FromResult(false);
        db.Execute("DELETE FROM board_people WHERE board=$board AND via<>''", ("board", board));
        db.Execute("DELETE FROM board_links WHERE board=$board", ("board", board));
        transaction.Commit();
        return Task.FromResult(true);
    }

    public Task<string?> LinkedBoard(string token, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Scalar("SELECT board FROM board_links WHERE token=$token", ("token", token)) as string);
    }

    public Task AddGuest(string token, string user, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("INSERT OR IGNORE INTO board_people(board, user, via) SELECT board, $user, token FROM board_links WHERE token=$token",
            ("token", token), ("user", user));
        return Task.CompletedTask;
    }

    private const string TemplateColumns = "id, name, folder, author, updated";

    private static TemplateInfo ReadTemplate(SqliteDataReader row) =>
        new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt64(4));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Task<TemplateInfo?> Publish(string author, string folder, string name, byte[] data, CancellationToken cancel)
    {
        using var db = database.Open();
        var id = Guid.NewGuid().ToString("N");
        var published = db.Execute($"""
            INSERT INTO templates(id, folder, author, name, data, updated)
            SELECT $id, $folder, $user, $name, $data, $now WHERE $folder='' OR $folder IN ({FoldersOf})
            """, ("id", id), ("folder", folder), ("user", author), ("name", name.Trim()), ("data", data), ("now", Now()));
        return Task.FromResult(published == 1 ? Single(db, id) : null);
    }

    public Task<TemplateInfo?> Update(string author, string id, string name, byte[] data, CancellationToken cancel)
    {
        using var db = database.Open();
        // Never older than the version it replaces, even if the clock went back.
        var updated = db.Execute("UPDATE templates SET name=$name, data=$data, updated=max(updated + 1, $now) WHERE id=$id AND author=$user",
            ("id", id), ("user", author), ("name", name.Trim()), ("data", data), ("now", Now()));
        return Task.FromResult(updated == 1 ? Single(db, id) : null);
    }

    public Task<bool> Remove(string user, bool admin, string id, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("DELETE FROM templates WHERE id=$id AND ($admin OR author=$user)",
            ("id", id), ("user", user), ("admin", admin)) == 1);
    }

    public Task<IReadOnlyList<SharedTemplate>> All(CancellationToken cancel)
    {
        using var db = database.Open();
        IReadOnlyList<SharedTemplate> rows = db.Rows("""
            SELECT templates.id, templates.name, templates.author, folders.name, templates.updated
            FROM templates LEFT JOIN folders ON folders.id=templates.folder ORDER BY templates.name
            """,
            row => new SharedTemplate(row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(4))));
        return Task.FromResult(rows);
    }

    private static TemplateInfo? Single(SqliteConnection db, string id) =>
        db.Rows($"SELECT {TemplateColumns} FROM templates WHERE id=$id", ReadTemplate, ("id", id)) is [var row] ? row : null;

    public Task<byte[]?> Find(string user, string id, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Scalar($"SELECT data FROM templates WHERE id=$id AND (folder='' OR folder IN ({FoldersOf}))",
            ("id", id), ("user", user)) as byte[]);
    }
}

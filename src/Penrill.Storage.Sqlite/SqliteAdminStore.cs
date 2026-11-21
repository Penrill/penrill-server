using System.IO.Compression;
using Penrill.Core.Admin;

namespace Penrill.Storage.Sqlite;

internal sealed class SqliteAdminStore(SqliteDatabase database) : IAdminStore, ISettingsStore
{
    public Task<IReadOnlyList<OrphanBoard>> Orphans(CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult<IReadOnlyList<OrphanBoard>>(db.Rows(
            "SELECT id, title, updated FROM boards WHERE id NOT IN (SELECT board FROM owners) ORDER BY updated DESC",
            row => new OrphanBoard(row.GetString(0), row.GetString(1), SqliteCommands.ParseStamp(row.GetString(2)))));
    }

    public Task<bool> AssignOwner(string board, string user, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("""
            INSERT OR IGNORE INTO owners(board, user)
            SELECT boards.id, accounts.name FROM boards, accounts WHERE boards.id=$board AND accounts.name=$user
            """, ("board", board), ("user", user)) == 1);
    }

    public Task<ServerCounts> Counts(CancellationToken cancel)
    {
        using var db = database.Open();
        var counts = db.Rows("SELECT (SELECT COUNT(*) FROM accounts), (SELECT COUNT(*) FROM boards), (SELECT COUNT(*) FROM credentials WHERE kind=1)",
            row => new ServerCounts(row.GetInt32(0), row.GetInt32(1), row.GetInt32(2)));
        return Task.FromResult(counts[0]);
    }

    /// <summary>
    /// <c>VACUUM INTO</c> gives a consistent snapshot while the server keeps running; assets are
    /// immutable files named by their hash, so copying them afterwards cannot tear.
    /// </summary>
    public async Task Backup(Stream output, CancellationToken cancel)
    {
        var snapshot = Path.Combine(Path.GetTempPath(), $"penrill-backup-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = database.Open()) db.Execute("VACUUM INTO $path", ("path", snapshot));
            await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await zip.CreateEntryFromFileAsync(snapshot, "boards.db", CompressionLevel.Optimal, cancel);
            var assets = Path.Combine(database.DataDirectory, "assets");
            if (Directory.Exists(assets))
                foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                    await zip.CreateEntryFromFileAsync(file, "assets/" + Path.GetRelativePath(assets, file).Replace('\\', '/'), CompressionLevel.NoCompression, cancel);
                }
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    public Task<string?> Get(string key, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Scalar("SELECT value FROM settings WHERE key=$key", ("key", key)) as string);
    }

    public Task Set(string key, string value, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("INSERT INTO settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("key", key), ("value", value));
        return Task.CompletedTask;
    }
}

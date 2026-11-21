using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Penrill.Storage.Sqlite;

internal sealed partial class SqliteMigrator(SqliteDatabase database, FileAssetStore assets, ILogger logger)
{
    private delegate void Migration(SqliteConnection db);

    private Migration[] Migrations => [CreateFirstSchema, MoveToChangeLog];

    public void Migrate()
    {
        using var db = database.Open();
        db.Execute("PRAGMA journal_mode=WAL;");
        var from = Convert.ToInt32(db.Scalar("PRAGMA user_version"));
        var migrations = Migrations;
        if (from > migrations.Length)
            throw new InvalidOperationException($"The database schema {from} is newer than this server supports ({migrations.Length}).");
        for (var version = from; version < migrations.Length; version++)
        {
            using var transaction = db.BeginTransaction();
            migrations[version](db);
            db.Execute($"PRAGMA user_version={version + 1}");
            transaction.Commit();
        }
        if (from < migrations.Length) LogMigrated(logger, database.DataDirectory, from, migrations.Length);
    }

    private static void CreateFirstSchema(SqliteConnection db)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS boards(id TEXT PRIMARY KEY, title TEXT NOT NULL, updated TEXT NOT NULL, revision INTEGER NOT NULL, board BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS accounts(name TEXT PRIMARY KEY, password TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(token TEXT PRIMARY KEY, user TEXT NOT NULL, expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS owners(board TEXT PRIMARY KEY, user TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS folders(id TEXT PRIMARY KEY, name TEXT NOT NULL, owner TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS members(folder TEXT NOT NULL, user TEXT NOT NULL, PRIMARY KEY(folder, user));
            CREATE TABLE IF NOT EXISTS folder_boards(folder TEXT NOT NULL, board TEXT NOT NULL, PRIMARY KEY(folder, board));
            CREATE TABLE IF NOT EXISTS templates(id TEXT PRIMARY KEY, folder TEXT NOT NULL, author TEXT NOT NULL, name TEXT NOT NULL, data BLOB NOT NULL);
            """);
        if ((long)db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='snapshots'")! == 0) return;
        db.Execute("INSERT OR IGNORE INTO boards(id, title, updated, revision, board) SELECT id, title, created, 1, board FROM snapshots; DROP TABLE snapshots;");
    }

    private void MoveToChangeLog(SqliteConnection db)
    {
        db.Execute("""
            ALTER TABLE boards RENAME TO legacy_boards;
            CREATE TABLE boards(
                id TEXT PRIMARY KEY, title TEXT NOT NULL, background TEXT NOT NULL, seq INTEGER NOT NULL,
                snapshot_seq INTEGER NOT NULL, snapshot BLOB NOT NULL, created TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE changes(
                board TEXT NOT NULL, seq INTEGER NOT NULL, id TEXT NOT NULL, author TEXT NOT NULL, at TEXT NOT NULL, body BLOB,
                PRIMARY KEY(board, seq), UNIQUE(board, id)) WITHOUT ROWID;
            CREATE TABLE assets(board TEXT NOT NULL, hash TEXT NOT NULL, type TEXT NOT NULL, size INTEGER NOT NULL, PRIMARY KEY(board, hash)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS owners_user ON owners(user);
            CREATE INDEX IF NOT EXISTS members_user ON members(user);
            CREATE INDEX IF NOT EXISTS folder_boards_board ON folder_boards(board);
            CREATE INDEX IF NOT EXISTS sessions_expires ON sessions(expires);
            """);
        var legacy = db.Rows("SELECT id, title, updated, revision, board FROM legacy_boards",
            row => (Id: row.GetString(0), Title: row.GetString(1), Updated: row.GetString(2), Revision: row.GetInt64(3), File: (byte[])row[4]));
        foreach (var board in legacy) ImportLegacyBoard(db, board.Id, board.Title, board.Updated, board.Revision, board.File);
        db.Execute("DROP TABLE legacy_boards; DROP TABLE IF EXISTS edit_receipts;");
    }

    private void ImportLegacyBoard(SqliteConnection db, string id, string title, string updated, long revision, byte[] file)
    {
        var root = JsonNode.Parse(file)!.AsObject();
        var files = root["assets"] as JsonObject;
        var items = root["items"]?.AsArray() ?? [];
        foreach (var item in items.OfType<JsonObject>())
        {
            if ((string?)item["type"] != "image") continue;
            var key = (string?)item["asset"];
            var source = (string?)item["src"] ?? (key is null ? null : (string?)files?[key]);
            item.Remove("src");
            if (source is null || DataUrl().Match(source) is not { Success: true } match)
            {
                LogLegacyImageSkipped(logger, id, (string?)item["id"] ?? "");
                continue;
            }
            var bytes = Convert.FromBase64String(match.Groups["data"].Value);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            assets.Save(hash, bytes, CancellationToken.None).GetAwaiter().GetResult();
            db.Execute("INSERT OR IGNORE INTO assets(board, hash, type, size) VALUES($board, $hash, $type, $size)",
                ("board", id), ("hash", hash), ("type", match.Groups["type"].Value), ("size", bytes.Length));
            item["asset"] = hash;
        }
        db.Execute("""
            INSERT INTO boards(id, title, background, seq, snapshot_seq, snapshot, created, updated)
            VALUES($id, $title, $background, $seq, $seq, $snapshot, $updated, $updated)
            """,
            ("id", id), ("title", title), ("background", (string?)root["background"] ?? "dots"), ("seq", revision),
            ("snapshot", JsonSerializer.SerializeToUtf8Bytes(items)), ("updated", updated));
    }

    [GeneratedRegex(@"\Adata:(?<type>image/[a-z0-9.+-]+);base64,(?<data>.+)\z", RegexOptions.Singleline)]
    private static partial Regex DataUrl();

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated the database in {Directory} from schema {From} to {To}")]
    private static partial void LogMigrated(ILogger logger, string directory, int from, int to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Board {Board}: image {Item} has no embedded data and was imported without it")]
    private static partial void LogLegacyImageSkipped(ILogger logger, string board, string item);
}

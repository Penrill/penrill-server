using Penrill.Core.Boards;

namespace Penrill.Storage.Sqlite;

internal sealed class SqliteBoardStore(SqliteDatabase database) : IBoardStore
{
    public Task<StoredBoard?> Load(string board, int recent, CancellationToken cancel)
    {
        using var db = database.Open();
        var head = db.Rows("SELECT title, background, seq, snapshot_seq, snapshot FROM boards WHERE id=$id",
            row => (Title: row.GetString(0), Background: row.GetString(1), Seq: row.GetInt64(2), SnapshotSeq: row.GetInt64(3), Items: (byte[])row[4]),
            ("id", board));
        if (head.Count == 0) return Task.FromResult<StoredBoard?>(null);
        var (title, background, seq, snapshotSeq, items) = head[0];
        var changes = db.Rows("SELECT seq, id, author, body FROM changes WHERE board=$id AND body IS NOT NULL AND seq > $from ORDER BY seq",
            row => new StoredChange(row.GetInt64(0), row.GetString(1), row.GetString(2), (byte[])row[3]),
            ("id", board), ("from", Math.Min(snapshotSeq, seq - recent)));
        return Task.FromResult<StoredBoard?>(new StoredBoard(board, title, background, seq, snapshotSeq, items, changes));
    }

    public Task<AppendOutcome> Append(AppendRequest request, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        if (db.Scalar("SELECT seq FROM changes WHERE board=$board AND id=$id", ("board", request.Board), ("id", request.ChangeId)) is long existing)
            return Task.FromResult(new AppendOutcome(true, existing));
        var at = SqliteCommands.Stamp(request.At);
        db.Execute("INSERT INTO changes(board, seq, id, author, at, body) VALUES($board, $seq, $id, $author, $at, $body)",
            ("board", request.Board), ("seq", request.Seq), ("id", request.ChangeId), ("author", request.Author), ("at", at), ("body", request.Body));
        var moved = db.Execute("UPDATE boards SET seq=$seq, title=$title, background=$background, updated=$at WHERE id=$board AND seq=$seq - 1",
            ("board", request.Board), ("seq", request.Seq), ("title", request.Title), ("background", request.Background), ("at", at));
        if (moved != 1) throw new InvalidOperationException($"Board {request.Board} is not at seq {request.Seq - 1}.");
        transaction.Commit();
        return Task.FromResult(new AppendOutcome(false, request.Seq));
    }

    public Task SaveSnapshot(SnapshotRecord snapshot, int retainBodies, TimeSpan retainReceipts, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        db.Execute("UPDATE boards SET snapshot=$items, snapshot_seq=$seq WHERE id=$board AND snapshot_seq < $seq",
            ("board", snapshot.Board), ("seq", snapshot.Seq), ("items", snapshot.Items));
        var compacted = snapshot.Seq - retainBodies;
        db.Execute("UPDATE changes SET body=NULL WHERE board=$board AND seq <= $compacted AND body IS NOT NULL",
            ("board", snapshot.Board), ("compacted", compacted));
        db.Execute("DELETE FROM changes WHERE board=$board AND seq <= $compacted AND at < $cutoff",
            ("board", snapshot.Board), ("compacted", compacted), ("cutoff", SqliteCommands.Stamp(snapshot.At - retainReceipts)));
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<bool> Create(NewBoard board, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var at = SqliteCommands.Stamp(board.At);
        var created = db.Execute("""
            INSERT OR IGNORE INTO boards(id, title, background, seq, snapshot_seq, snapshot, created, updated)
            VALUES($id, $title, $background, 0, 0, $items, $at, $at)
            """,
            ("id", board.Id), ("title", board.Content.Title), ("background", board.Content.Background),
            ("items", board.Content.SerializeItems()), ("at", at)) == 1;
        if (created) db.Execute("INSERT OR REPLACE INTO owners(board, user) VALUES($id, $user)", ("id", board.Id), ("user", board.Owner));
        transaction.Commit();
        return Task.FromResult(created);
    }

    public Task<IReadOnlyList<BoardSummary>> List(string user, CancellationToken cancel)
    {
        using var db = database.Open();
        IReadOnlyList<BoardSummary> boards = db.Rows(
            $"""
            SELECT boards.id, boards.title, boards.seq, boards.updated, coalesce(owners.user, '')
            FROM boards LEFT JOIN owners ON owners.board=boards.id
            WHERE boards.id IN ({SqlAccess.BoardsOf}) ORDER BY boards.updated DESC, boards.id
            """,
            row => new BoardSummary(row.GetString(0), row.GetString(1), row.GetInt64(2), SqliteCommands.ParseStamp(row.GetString(3)), row.GetString(4)),
            ("user", user));
        return Task.FromResult(boards);
    }

    public Task<BoardAccess> Delete(string board, string user, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        if (db.Scalar("SELECT 1 FROM boards WHERE id=$id", ("id", board)) is null) return Task.FromResult(BoardAccess.Missing);
        if (db.Scalar("SELECT user FROM owners WHERE board=$id", ("id", board)) is not string owner || owner != user)
            return Task.FromResult(BoardAccess.Denied);
        foreach (var table in (string[])["changes", "assets"]) db.Execute($"DELETE FROM {table} WHERE board=$id", ("id", board));
        foreach (var table in (string[])["owners", "folder_boards"]) db.Execute($"DELETE FROM {table} WHERE board=$id", ("id", board));
        db.Execute("DELETE FROM boards WHERE id=$id", ("id", board));
        transaction.Commit();
        return Task.FromResult(BoardAccess.Allowed);
    }

    public Task AddAsset(string board, AssetInfo asset, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("INSERT OR IGNORE INTO assets(board, hash, type, size) VALUES($board, $hash, $type, $size)",
            ("board", board), ("hash", asset.Hash), ("type", asset.ContentType), ("size", asset.Size));
        return Task.CompletedTask;
    }

    public Task<AssetInfo?> FindAsset(string board, string hash, CancellationToken cancel)
    {
        using var db = database.Open();
        var rows = db.Rows("SELECT type, size FROM assets WHERE board=$board AND hash=$hash",
            row => new AssetInfo(hash, row.GetString(0), row.GetInt64(1)), ("board", board), ("hash", hash));
        return Task.FromResult(rows.FirstOrDefault());
    }
}

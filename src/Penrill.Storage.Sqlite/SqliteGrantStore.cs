using Microsoft.Data.Sqlite;
using Penrill.Core.Accounts;

namespace Penrill.Storage.Sqlite;

internal sealed class SqliteGrantStore(SqliteDatabase database) : IGrantStore
{
    private const string Columns = "id, token, kind, subject, created_by, created, expires, uses_left";
    private const string Valid = "uses_left > 0 AND (expires IS NULL OR expires > $now)";

    public Task Create(Grant grant, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute($"INSERT INTO grants({Columns}) VALUES($id, $token, $kind, $subject, $by, $created, $expires, $uses)",
            ("id", grant.Id), ("token", grant.TokenHash), ("kind", (int)grant.Kind), ("subject", grant.Subject), ("by", grant.CreatedBy),
            ("created", SqliteCommands.Stamp(grant.Created)), ("expires", grant.Expires?.ToUnixTimeSeconds()), ("uses", grant.UsesLeft));
        return Task.CompletedTask;
    }

    public Task<Grant?> Find(string tokenHash, DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Rows($"SELECT {Columns} FROM grants WHERE token=$token AND {Valid}", Read,
            ("token", tokenHash), ("now", now.ToUnixTimeSeconds())).FirstOrDefault());
    }

    public Task<bool> TryUse(string tokenHash, DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute($"UPDATE grants SET uses_left=uses_left-1 WHERE token=$token AND {Valid}",
            ("token", tokenHash), ("now", now.ToUnixTimeSeconds())) == 1);
    }

    public Task Refund(string tokenHash, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("UPDATE grants SET uses_left=uses_left+1 WHERE token=$token", ("token", tokenHash));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Grant>> List(GrantKind kind, DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM grants WHERE uses_left <= 0 OR (expires IS NOT NULL AND expires <= $now)", ("now", now.ToUnixTimeSeconds()));
        return Task.FromResult<IReadOnlyList<Grant>>(db.Rows($"SELECT {Columns} FROM grants WHERE kind=$kind ORDER BY created DESC", Read, ("kind", (int)kind)));
    }

    public Task<bool> Delete(string id, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("DELETE FROM grants WHERE id=$id", ("id", id)) == 1);
    }

    public Task DeleteKind(GrantKind kind, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM grants WHERE kind=$kind", ("kind", (int)kind));
        return Task.CompletedTask;
    }

    private static Grant Read(SqliteDataReader row) => new(
        row.GetString(0), row.GetString(1), (GrantKind)row.GetInt32(2), row.GetString(3), row.GetString(4), SqliteCommands.ParseStamp(row.GetString(5)),
        row.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(6)), row.GetInt32(7));
}

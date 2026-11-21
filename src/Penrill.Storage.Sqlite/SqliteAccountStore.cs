using Penrill.Core.Accounts;

namespace Penrill.Storage.Sqlite;

internal sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore
{
    public Task<bool> Create(string name, string passwordHash, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("INSERT OR IGNORE INTO accounts(name, password) VALUES($name, $hash)", ("name", name), ("hash", passwordHash)) == 1);
    }

    public Task<string?> PasswordHash(string name, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Scalar("SELECT password FROM accounts WHERE name=$name", ("name", name)) as string);
    }

    public Task CreateSession(string tokenHash, string user, DateTimeOffset expires, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("INSERT INTO sessions(token, user, expires) VALUES($token, $user, $expires)",
            ("token", tokenHash), ("user", user), ("expires", expires.ToUnixTimeSeconds()));
        return Task.CompletedTask;
    }

    public Task<AccountSession?> FindSession(string tokenHash, DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        var rows = db.Rows("SELECT user, expires FROM sessions WHERE token=$token AND expires > $now",
            row => new AccountSession(row.GetString(0), tokenHash, DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(1))),
            ("token", tokenHash), ("now", now.ToUnixTimeSeconds()));
        return Task.FromResult(rows.FirstOrDefault());
    }

    public Task DeleteSession(string tokenHash, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM sessions WHERE token=$token", ("token", tokenHash));
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredSessions(DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("DELETE FROM sessions WHERE expires <= $now", ("now", now.ToUnixTimeSeconds())));
    }
}

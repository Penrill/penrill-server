using Microsoft.Data.Sqlite;
using Penrill.Core.Accounts;

namespace Penrill.Storage.Sqlite;

internal sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore
{
    public Task<bool> Create(string name, string passwordHash, Role role, DateTimeOffset at, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("INSERT OR IGNORE INTO accounts(name, password, role, created) VALUES($name, $hash, $role, $at)",
            ("name", name), ("hash", passwordHash), ("role", (int)role), ("at", SqliteCommands.Stamp(at))) == 1);
    }

    public Task<string?> PasswordHash(string name, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Scalar("SELECT password FROM accounts WHERE name=$name AND disabled=0", ("name", name)) as string);
    }

    public Task<bool> SetPassword(string name, string passwordHash, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("UPDATE accounts SET password=$hash WHERE name=$name", ("name", name), ("hash", passwordHash)) == 1);
    }

    public Task<AccountInfo?> Find(string name, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Rows("SELECT name, role, disabled, created FROM accounts WHERE name=$name", ReadAccount, ("name", name)).FirstOrDefault());
    }

    public Task<IReadOnlyList<AccountInfo>> List(CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult<IReadOnlyList<AccountInfo>>(db.Rows("SELECT name, role, disabled, created FROM accounts ORDER BY role DESC, name", ReadAccount));
    }

    public Task<bool> SetRole(string name, Role role, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("UPDATE accounts SET role=$role WHERE name=$name", ("name", name), ("role", (int)role)) == 1);
    }

    public Task<bool> SetDisabled(string name, bool disabled, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("UPDATE accounts SET disabled=$disabled WHERE name=$name", ("name", name), ("disabled", disabled ? 1 : 0)) == 1);
    }

    public Task<bool> Delete(string name, CancellationToken cancel)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        if (db.Scalar("SELECT role FROM accounts WHERE name=$name", ("name", name)) is not long role || (Role)role == Role.Owner)
            return Task.FromResult(false);
        db.Execute("DELETE FROM credentials WHERE user=$name", ("name", name));
        db.Execute("DELETE FROM members WHERE user=$name", ("name", name));
        db.Execute("DELETE FROM board_people WHERE user=$name", ("name", name));
        db.Execute("DELETE FROM owners WHERE user=$name", ("name", name));
        var deleted = db.Execute("DELETE FROM accounts WHERE name=$name", ("name", name)) == 1;
        transaction.Commit();
        return Task.FromResult(deleted);
    }

    public Task<bool> HasOwner(CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult((long)db.Scalar("SELECT EXISTS(SELECT 1 FROM accounts WHERE role=$owner)", ("owner", (int)Role.Owner))! == 1);
    }

    public Task CreateCredential(NewCredential credential, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("""
            INSERT INTO credentials(token, id, user, kind, device, created, expires)
            VALUES($token, $id, $user, $kind, $device, $created, $expires)
            """,
            ("token", credential.TokenHash), ("id", credential.Id), ("user", credential.User), ("kind", (int)credential.Kind),
            ("device", credential.Device), ("created", SqliteCommands.Stamp(credential.Created)), ("expires", credential.Expires?.ToUnixTimeSeconds()));
        return Task.CompletedTask;
    }

    public Task<FoundCredential?> FindCredential(string tokenHash, DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        var rows = db.Rows("""
            SELECT credentials.user, accounts.role, credentials.id, credentials.kind, credentials.expires, credentials.last_used
            FROM credentials JOIN accounts ON accounts.name=credentials.user
            WHERE credentials.token=$token AND accounts.disabled=0 AND (credentials.expires IS NULL OR credentials.expires > $now)
            """,
            row => new FoundCredential(
                new AccountSession(row.GetString(0), (Role)row.GetInt32(1), tokenHash, row.GetString(2), (CredentialKind)row.GetInt32(3),
                    row.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(4))),
                row.IsDBNull(5) ? null : SqliteCommands.ParseStamp(row.GetString(5))),
            ("token", tokenHash), ("now", now.ToUnixTimeSeconds()));
        return Task.FromResult(rows.FirstOrDefault());
    }

    public Task Touch(string tokenHash, DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("UPDATE credentials SET last_used=$now WHERE token=$token", ("token", tokenHash), ("now", SqliteCommands.Stamp(now)));
        return Task.CompletedTask;
    }

    public Task DeleteCredential(string tokenHash, CancellationToken cancel)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM credentials WHERE token=$token", ("token", tokenHash));
        return Task.CompletedTask;
    }

    public Task<string?> DeleteDevice(string? user, string id, CancellationToken cancel)
    {
        using var db = database.Open();
        var hash = db.Scalar("DELETE FROM credentials WHERE id=$id AND kind=$kind AND ($user IS NULL OR user=$user) RETURNING token",
            ("id", id), ("kind", (int)CredentialKind.Device), ("user", user)) as string;
        return Task.FromResult(hash);
    }

    public Task<IReadOnlyList<string>> DeleteCredentialsOf(string user, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult<IReadOnlyList<string>>(db.Rows("DELETE FROM credentials WHERE user=$user RETURNING token", row => row.GetString(0), ("user", user)));
    }

    public Task<IReadOnlyList<DeviceInfo>> Devices(string? user, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(db.Rows("""
            SELECT id, user, device, created, last_used FROM credentials
            WHERE kind=$kind AND ($user IS NULL OR user=$user) ORDER BY user, created DESC
            """,
            row => new DeviceInfo(row.GetString(0), row.GetString(1), row.GetString(2), SqliteCommands.ParseStamp(row.GetString(3)),
                row.IsDBNull(4) ? null : SqliteCommands.ParseStamp(row.GetString(4))),
            ("kind", (int)CredentialKind.Device), ("user", user)));
    }

    public Task<int> DeleteExpired(DateTimeOffset now, CancellationToken cancel)
    {
        using var db = database.Open();
        return Task.FromResult(db.Execute("DELETE FROM credentials WHERE expires IS NOT NULL AND expires <= $now", ("now", now.ToUnixTimeSeconds())));
    }

    private static AccountInfo ReadAccount(SqliteDataReader row) =>
        new(row.GetString(0), (Role)row.GetInt32(1), row.GetInt32(2) != 0, row.IsDBNull(3) ? null : SqliteCommands.ParseStamp(row.GetString(3)));
}

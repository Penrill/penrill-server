using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Penrill.Core.Telemetry;

namespace Penrill.Core.Accounts;

public enum Role { Member = 0, Admin = 1, Owner = 2 }

/// <summary>A web session comes from a password sign-in and expires; a device token comes from an approved link and lives until revoked.</summary>
public enum CredentialKind { Session = 0, Device = 1 }

public sealed record AccountSession(string User, Role Role, string TokenHash, string CredentialId, CredentialKind Kind, DateTimeOffset? Expires);

public sealed record AccountInfo(string Name, Role Role, bool Disabled, DateTimeOffset? Created);

public sealed record DeviceInfo(string Id, string User, string Name, DateTimeOffset Created, DateTimeOffset? LastUsed);

public sealed record NewCredential(
    string TokenHash, string Id, string User, CredentialKind Kind, string Device, DateTimeOffset Created, DateTimeOffset? Expires);

public sealed record FoundCredential(AccountSession Session, DateTimeOffset? LastUsed);

public interface IAccountStore
{
    Task<bool> Create(string name, string passwordHash, Role role, DateTimeOffset at, CancellationToken cancel);
    /// <summary>The password hash of an enabled account.</summary>
    Task<string?> PasswordHash(string name, CancellationToken cancel);
    Task<bool> SetPassword(string name, string passwordHash, CancellationToken cancel);
    Task<AccountInfo?> Find(string name, CancellationToken cancel);
    Task<IReadOnlyList<AccountInfo>> List(CancellationToken cancel);
    Task<bool> SetRole(string name, Role role, CancellationToken cancel);
    Task<bool> SetDisabled(string name, bool disabled, CancellationToken cancel);
    Task<bool> Delete(string name, CancellationToken cancel);
    Task<bool> HasOwner(CancellationToken cancel);

    Task CreateCredential(NewCredential credential, CancellationToken cancel);
    /// <summary>A credential that has not expired and belongs to an enabled account.</summary>
    Task<FoundCredential?> FindCredential(string tokenHash, DateTimeOffset now, CancellationToken cancel);
    Task Touch(string tokenHash, DateTimeOffset now, CancellationToken cancel);
    Task DeleteCredential(string tokenHash, CancellationToken cancel);
    /// <summary>Deletes a device token, optionally only one of <paramref name="user"/>, and returns its token hash.</summary>
    Task<string?> DeleteDevice(string? user, string id, CancellationToken cancel);
    Task<IReadOnlyList<string>> DeleteCredentialsOf(string user, CancellationToken cancel);
    Task<IReadOnlyList<DeviceInfo>> Devices(string? user, CancellationToken cancel);
    Task<int> DeleteExpired(DateTimeOffset now, CancellationToken cancel);
}

public interface IPasswordHashing
{
    string Hash(string user, string password);
    bool Verify(string user, string hash, string password);
}

public sealed class AccountOptions
{
    /// <summary>Anyone who reaches the server may create an account. Penrill Cloud turns it on; a self-hosted server uses invites.</summary>
    public bool OpenRegistration { get; set; }
}

public enum SignInStatus { Ok, Invalid, Taken, Denied, Closed }

public readonly record struct SignInResult(SignInStatus Status, string? Name = null, string? Token = null);

public sealed partial class AccountService(IAccountStore store, IPasswordHashing passwords, TimeProvider time, AccountOptions options, ILogger<AccountService> logger)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);
    public const int MaxDeviceName = 64;

    private readonly Lazy<string> unknownUserHash = new(() => passwords.Hash("", Guid.NewGuid().ToString()));

    /// <summary>Raised with the token hash of every credential that stops being valid, so open connections can close (Observer).</summary>
    public event Action<string>? CredentialRevoked;

    public bool OpenRegistration => options.OpenRegistration;

    public async Task<SignInResult> Register(string? name, string? password, CancellationToken cancel)
    {
        if (!options.OpenRegistration) return new(SignInStatus.Closed);
        var created = await CreateAccount(name, password, Role.Member, cancel);
        return created.Status == SignInStatus.Ok ? await StartSession(created.Name!, cancel) : created;
    }

    /// <summary>Creates an account without signing in. Used by registration, invites and the server claim.</summary>
    public async Task<SignInResult> CreateAccount(string? name, string? password, Role role, CancellationToken cancel)
    {
        if (Normalize(name) is not { } user || !IsPassword(password)) return new(SignInStatus.Invalid);
        if (!await store.Create(user, passwords.Hash(user, password!), role, time.GetUtcNow(), cancel)) return new(SignInStatus.Taken);
        LogRegistered(logger, user, role);
        return new(SignInStatus.Ok, user);
    }

    public async Task<SignInResult> SignIn(string? name, string? password, CancellationToken cancel)
    {
        if (await CheckPassword(name, password, cancel) is not { } user) return new(SignInStatus.Denied);
        return await StartSession(user, cancel);
    }

    public async Task<string?> CheckPassword(string? name, string? password, CancellationToken cancel)
    {
        if (Normalize(name) is not { } user || !IsPassword(password)) return null;
        var hash = await store.PasswordHash(user, cancel);
        if (!passwords.Verify(user, hash ?? unknownUserHash.Value, password!) || hash is null)
        {
            PenrillTelemetry.AuthFailures.Add(1, PenrillTelemetry.Tag("kind", "password"));
            LogSignInFailed(logger, user);
            return null;
        }
        return user;
    }

    public async Task<bool> ChangePassword(string user, string? current, string? next, CancellationToken cancel) =>
        await CheckPassword(user, current, cancel) is not null && await SetPassword(user, next, cancel);

    public async Task<bool> SetPassword(string user, string? password, CancellationToken cancel) =>
        IsPassword(password) && await store.SetPassword(user, passwords.Hash(user, password!), cancel);

    /// <summary>A token for an editor, created when its link request is approved.</summary>
    public async Task<string> IssueDeviceToken(string user, string device, CancellationToken cancel)
    {
        var token = NewToken();
        await store.CreateCredential(new NewCredential(
            HashToken(token), NewId(), user, CredentialKind.Device, DeviceName(device), time.GetUtcNow(), null), cancel);
        LogDeviceLinked(logger, user, DeviceName(device));
        return token;
    }

    public async Task<AccountSession?> Authenticate(string token, CancellationToken cancel)
    {
        if (token.Length != 64) return null;
        var hash = HashToken(token);
        var now = time.GetUtcNow();
        if (await store.FindCredential(hash, now, cancel) is not { } found) return null;
        if (found.LastUsed is not { } last || now - last > TouchEvery) await store.Touch(hash, now, cancel);
        return found.Session;
    }

    public Task<AccountInfo?> Find(string user, CancellationToken cancel) => store.Find(user, cancel);

    public Task<IReadOnlyList<DeviceInfo>> Devices(string? user, CancellationToken cancel) => store.Devices(user, cancel);

    public async Task<bool> RevokeDevice(string? user, string id, CancellationToken cancel)
    {
        if (await store.DeleteDevice(user, id, cancel) is not { } hash) return false;
        CredentialRevoked?.Invoke(hash);
        return true;
    }

    public async Task SignOut(string tokenHash, CancellationToken cancel)
    {
        await store.DeleteCredential(tokenHash, cancel);
        CredentialRevoked?.Invoke(tokenHash);
    }

    public async Task<bool> SetDisabled(string user, bool disabled, CancellationToken cancel)
    {
        if (await store.Find(user, cancel) is not { Role: not Role.Owner }) return false;
        if (!await store.SetDisabled(user, disabled, cancel)) return false;
        if (disabled) await RevokeAll(user, cancel);
        return true;
    }

    public async Task<bool> DeleteAccount(string user, CancellationToken cancel)
    {
        if (await store.Find(user, cancel) is not { Role: not Role.Owner }) return false;
        await RevokeAll(user, cancel);
        return await store.Delete(user, cancel);
    }

    public async Task RevokeAll(string user, CancellationToken cancel)
    {
        foreach (var hash in await store.DeleteCredentialsOf(user, cancel)) CredentialRevoked?.Invoke(hash);
    }

    public Task<int> DeleteExpiredSessions(CancellationToken cancel) => store.DeleteExpired(time.GetUtcNow(), cancel);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string? Normalize(string? name)
    {
        var user = name?.Trim().ToLowerInvariant();
        return user is not null && UserName().IsMatch(user) ? user : null;
    }

    public static bool IsPassword(string? password) => password is { Length: >= 10 and <= 256 };

    public static string DeviceName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Unnamed device" : clean.Length > MaxDeviceName ? clean[..MaxDeviceName] : clean;
    }

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private async Task<SignInResult> StartSession(string user, CancellationToken cancel)
    {
        var token = NewToken();
        var now = time.GetUtcNow();
        await store.CreateCredential(new NewCredential(HashToken(token), NewId(), user, CredentialKind.Session, "", now, now + SessionLifetime), cancel);
        return new(SignInStatus.Ok, user, token);
    }

    [GeneratedRegex(@"\A[a-z0-9_-]{3,32}\z")]
    private static partial Regex UserName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created account {User} as {Role}")]
    private static partial void LogRegistered(ILogger logger, string user, Role role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked device {Device} to {User}")]
    private static partial void LogDeviceLinked(ILogger logger, string user, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused sign-in for {User}")]
    private static partial void LogSignInFailed(ILogger logger, string user);
}

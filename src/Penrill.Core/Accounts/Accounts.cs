using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Penrill.Core.Telemetry;

namespace Penrill.Core.Accounts;

public sealed record AccountSession(string User, string TokenHash, DateTimeOffset Expires);

public interface IAccountStore
{
    Task<bool> Create(string name, string passwordHash, CancellationToken cancel);
    Task<string?> PasswordHash(string name, CancellationToken cancel);
    Task CreateSession(string tokenHash, string user, DateTimeOffset expires, CancellationToken cancel);
    Task<AccountSession?> FindSession(string tokenHash, DateTimeOffset now, CancellationToken cancel);
    Task DeleteSession(string tokenHash, CancellationToken cancel);
    Task<int> DeleteExpiredSessions(DateTimeOffset now, CancellationToken cancel);
}

public interface IPasswordHashing
{
    string Hash(string user, string password);
    bool Verify(string user, string hash, string password);
}

public enum SignInStatus { Ok, Invalid, Taken, Denied }

public readonly record struct SignInResult(SignInStatus Status, string? Name = null, string? Token = null);

public sealed partial class AccountService(IAccountStore store, IPasswordHashing passwords, TimeProvider time, ILogger<AccountService> logger)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private readonly Lazy<string> unknownUserHash = new(() => passwords.Hash("", Guid.NewGuid().ToString()));

    public async Task<SignInResult> Register(string? name, string? password, CancellationToken cancel)
    {
        if (Normalize(name) is not { } user || !IsPassword(password)) return new(SignInStatus.Invalid);
        if (!await store.Create(user, passwords.Hash(user, password!), cancel)) return new(SignInStatus.Taken);
        LogRegistered(logger, user);
        return await StartSession(user, cancel);
    }

    public async Task<SignInResult> SignIn(string? name, string? password, CancellationToken cancel)
    {
        if (Normalize(name) is not { } user || !IsPassword(password)) return new(SignInStatus.Invalid);
        var hash = await store.PasswordHash(user, cancel);
        if (!passwords.Verify(user, hash ?? unknownUserHash.Value, password!) || hash is null)
        {
            PenrillTelemetry.AuthFailures.Add(1, PenrillTelemetry.Tag("kind", "password"));
            LogSignInFailed(logger, user);
            return new(SignInStatus.Denied);
        }
        return await StartSession(user, cancel);
    }

    public async Task<AccountSession?> Authenticate(string token, CancellationToken cancel)
    {
        if (token.Length != 64) return null;
        return await store.FindSession(HashToken(token), time.GetUtcNow(), cancel);
    }

    public Task SignOut(string tokenHash, CancellationToken cancel) => store.DeleteSession(tokenHash, cancel);

    public Task<int> DeleteExpiredSessions(CancellationToken cancel) => store.DeleteExpiredSessions(time.GetUtcNow(), cancel);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<SignInResult> StartSession(string user, CancellationToken cancel)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await store.CreateSession(HashToken(token), user, time.GetUtcNow() + SessionLifetime, cancel);
        return new(SignInStatus.Ok, user, token);
    }

    private static string? Normalize(string? name)
    {
        var user = name?.Trim().ToLowerInvariant();
        return user is not null && UserName().IsMatch(user) ? user : null;
    }

    private static bool IsPassword(string? password) => password is { Length: >= 10 and <= 256 };

    [GeneratedRegex(@"\A[a-z0-9_-]{3,32}\z")]
    private static partial Regex UserName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered account {User}")]
    private static partial void LogRegistered(ILogger logger, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused sign-in for {User}")]
    private static partial void LogSignInFailed(ILogger logger, string user);
}

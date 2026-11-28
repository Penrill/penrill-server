using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Application.Common.Interfaces;
using Application.Common.Models;
using Sync.Live;

namespace Application.Accounts;

public sealed record SignedIn(string Name, string Token);

/// <summary>Who a bearer token belongs to, as the authentication handler and live connections see it.</summary>
public sealed record AuthenticatedSession(string User, Role Role, string CredentialId, CredentialKind Kind, DateTimeOffset? Expires);

public sealed record DeviceDto(string Id, string User, string Name, DateTimeOffset Created, DateTimeOffset? LastUsed);

/// <summary>
/// Bearer tokens: sessions from a password sign-in, device tokens from an approved link. Tokens are 32 random bytes
/// in hex and only their SHA-256 is stored, so a copy of the database cannot be used to sign in.
/// </summary>
public sealed partial class AccountService(
    IApplicationDbContext context, IIdentityService identity, IPublisher publisher, TimeProvider time, ILogger<AccountService> logger)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);

    /// <summary>Creates an account without signing in. Used by registration, invites, the server claim and administrators.</summary>
    public async Task<(Result Result, string? User)> CreateAccount(string? name, string? password, Role role)
    {
        if (AccountRules.NormalizeUserName(name) is not { } user || !AccountRules.IsPassword(password))
            return (Result.Failure([AccountErrors.Invalid]), null);
        var (result, id) = await identity.CreateUserAsync(user, password!, role);
        if (!result.Succeeded) return (result, null);
        LogRegistered(logger, id, role);
        return (result, id);
    }

    /// <summary>The account id when the name and password match an enabled account.</summary>
    public async Task<string?> CheckPassword(string? name, string? password)
    {
        if (AccountRules.NormalizeUserName(name) is not { } user || !AccountRules.IsPassword(password)) return null;
        if (await identity.CheckPasswordAsync(user, password!) is { } id) return id;
        SyncTelemetry.AuthFailures.Add(1, SyncTelemetry.Tag("kind", "password"));
        LogSignInFailed(logger, user);
        return null;
    }

    public async Task<bool> SetPassword(string user, string? password) =>
        AccountRules.IsPassword(password) && (await identity.SetPasswordAsync(user, password!)).Succeeded;

    /// <summary>Changes the password when the current one is right. Other sessions stay signed in.</summary>
    public async Task<bool> ChangePassword(string user, string? current, string? next) =>
        await CheckPassword(user, current) is not null && await SetPassword(user, next);

    public async Task<SignedIn> StartSession(string user, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var now = time.GetUtcNow();
        context.Credentials.Add(new Credential
        {
            Id = NewId(), TokenHash = HashToken(token), UserId = user, Kind = CredentialKind.Session, Created = now, Expires = now + SessionLifetime
        });
        await context.SaveChangesAsync(cancellationToken);
        return new SignedIn(user, token);
    }

    /// <summary>A token for an editor, created when its link request is approved or a guest joins by a draw link.</summary>
    public async Task<string> IssueDeviceToken(string user, string? device, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var name = AccountRules.DeviceName(device);
        context.Credentials.Add(new Credential
        {
            Id = NewId(), TokenHash = HashToken(token), UserId = user, Kind = CredentialKind.Device, Device = name, Created = time.GetUtcNow()
        });
        await context.SaveChangesAsync(cancellationToken);
        LogDeviceLinked(logger, user, name);
        return token;
    }

    public async Task<AuthenticatedSession?> Authenticate(string token, CancellationToken cancellationToken)
    {
        var trimmed = token.Trim();
        if (trimmed.Length != 64) return null;
        var hash = HashToken(trimmed);
        var now = time.GetUtcNow();
        var credential = await context.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);
        if (credential is null || !credential.IsValidAt(now)) return null;
        if (await identity.FindAsync(credential.UserId) is not { Disabled: false } account) return null;
        if (credential.LastUsed is not { } last || now - last > TouchEvery)
        {
            try
            {
                await context.Credentials
                    .Where(c => c.Id == credential.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.LastUsed, now), cancellationToken);
            }
            catch
            {
                // Best-effort timestamp touch; never fail authentication on concurrent requests.
            }
        }
        return new AuthenticatedSession(credential.UserId, account.Role, credential.Id, credential.Kind, credential.Expires);
    }

    public async Task<IReadOnlyList<DeviceDto>> Devices(string? user, CancellationToken cancellationToken) =>
        (await context.Credentials.AsNoTracking()
            .Where(c => c.Kind == CredentialKind.Device && (user == null || c.UserId == user))
            .Select(c => new DeviceDto(c.Id, c.UserId, c.Device, c.Created, c.LastUsed))
            .ToListAsync(cancellationToken))
        .OrderBy(d => d.User, StringComparer.Ordinal).ThenByDescending(d => d.Created).ToList();

    /// <summary>Revokes a device token, optionally only one of <paramref name="user"/>.</summary>
    public async Task<bool> RevokeDevice(string? user, string id, CancellationToken cancellationToken)
    {
        var deleted = await context.Credentials
            .Where(c => c.Id == id && c.Kind == CredentialKind.Device && (user == null || c.UserId == user))
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0) return false;
        await publisher.Publish(new CredentialRevokedEvent(id), cancellationToken);
        return true;
    }

    public async Task SignOut(string credentialId, CancellationToken cancellationToken)
    {
        await context.Credentials.Where(c => c.Id == credentialId).ExecuteDeleteAsync(cancellationToken);
        await publisher.Publish(new CredentialRevokedEvent(credentialId), cancellationToken);
    }

    /// <summary>Signs out the credential a token belongs to, if any.</summary>
    public async Task SignOutToken(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        if (await context.Credentials.Where(c => c.TokenHash == hash).Select(c => c.Id).FirstOrDefaultAsync(cancellationToken) is { } id)
            await SignOut(id, cancellationToken);
    }

    /// <summary>Signs the account out everywhere: after a password change, when it is disabled or deleted.</summary>
    public async Task RevokeAll(string user, CancellationToken cancellationToken)
    {
        var ids = await context.Credentials.Where(c => c.UserId == user).Select(c => c.Id).ToListAsync(cancellationToken);
        await context.Credentials.Where(c => c.UserId == user).ExecuteDeleteAsync(cancellationToken);
        foreach (var id in ids) await publisher.Publish(new CredentialRevokedEvent(id), cancellationToken);
    }

    public async Task<bool> SetDisabled(string user, bool disabled, CancellationToken cancellationToken)
    {
        if (await identity.FindAsync(user) is not { Role: not Role.Owner }) return false;
        if (!await identity.SetDisabledAsync(user, disabled)) return false;
        if (disabled) await RevokeAll(user, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAccount(string user, CancellationToken cancellationToken)
    {
        if (await identity.FindAsync(user) is not { Role: not Role.Owner }) return false;
        await RevokeAll(user, cancellationToken);
        return (await identity.DeleteUserAsync(user)).Succeeded;
    }

    public async Task<int> DeleteExpiredSessions(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await context.Credentials.Where(c => c.Expires != null && c.Expires <= now).ExecuteDeleteAsync(cancellationToken);
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    [LoggerMessage(Level = LogLevel.Information, Message = "Created account {User} as {Role}")]
    private static partial void LogRegistered(ILogger logger, string user, Role role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked device {Device} to {User}")]
    private static partial void LogDeviceLinked(ILogger logger, string user, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused sign-in for {User}")]
    private static partial void LogSignInFailed(ILogger logger, string user);
}

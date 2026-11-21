using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Penrill.Core.Accounts;

/// <summary>
/// A one-time link that lets someone do one thing without an account or without their password:
/// claim a fresh server, join it by invite, or set a new password.
/// </summary>
public enum GrantKind { Claim = 0, Invite = 1, Reset = 2 }

/// <param name="Subject">The role an invite gives, the user a reset is for, empty for a claim.</param>
public sealed record Grant(string Id, string TokenHash, GrantKind Kind, string Subject, string CreatedBy, DateTimeOffset Created, DateTimeOffset? Expires, int UsesLeft);

public interface IGrantStore
{
    Task Create(Grant grant, CancellationToken cancel);
    Task<Grant?> Find(string tokenHash, DateTimeOffset now, CancellationToken cancel);
    /// <summary>Spends one use, atomically. False when the grant is gone, expired or used up.</summary>
    Task<bool> TryUse(string tokenHash, DateTimeOffset now, CancellationToken cancel);
    /// <summary>Gives back a use spent by <see cref="TryUse"/> when the action behind it failed.</summary>
    Task Refund(string tokenHash, CancellationToken cancel);
    Task<IReadOnlyList<Grant>> List(GrantKind kind, DateTimeOffset now, CancellationToken cancel);
    Task<bool> Delete(string id, CancellationToken cancel);
    Task DeleteKind(GrantKind kind, CancellationToken cancel);
}

public sealed record GrantToken(string Token, Grant Grant);

public enum RedeemStatus { Ok, Invalid, Expired, Taken }

public readonly record struct RedeemResult(RedeemStatus Status, string? User = null);

public sealed partial class GrantService(IGrantStore grants, AccountService accounts, IAccountStore store, TimeProvider time, ILogger<GrantService> logger)
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultInviteLifetime = TimeSpan.FromDays(7);

    /// <summary>True until someone claims the server and becomes its owner.</summary>
    public async Task<bool> Unclaimed(CancellationToken cancel) => !await store.HasOwner(cancel);

    /// <summary>A new claim link; earlier ones stop working. Null when the server already has an owner.</summary>
    public async Task<string?> CreateClaim(CancellationToken cancel)
    {
        if (!await Unclaimed(cancel)) return null;
        await grants.DeleteKind(GrantKind.Claim, cancel);
        return (await Create(GrantKind.Claim, "", "", null, 1, cancel)).Token;
    }

    public async Task<RedeemResult> Claim(string? token, string? name, string? password, CancellationToken cancel)
    {
        if (!await Unclaimed(cancel)) return new(RedeemStatus.Expired);
        return await Redeem(token, GrantKind.Claim, cancel, async _ =>
        {
            var created = await accounts.CreateAccount(name, password, Role.Owner, cancel);
            // A server upgraded from before roles already has accounts: one of them may take ownership.
            if (created.Status == SignInStatus.Taken && await accounts.CheckPassword(name, password, cancel) is { } existing &&
                await store.SetRole(existing, Role.Owner, cancel))
                created = new(SignInStatus.Ok, existing);
            if (created.Status == SignInStatus.Ok) LogClaimed(logger, created.Name!);
            return created;
        });
    }

    public Task<GrantToken> CreateInvite(string by, Role role, TimeSpan? lifetime, int uses, CancellationToken cancel) =>
        Create(GrantKind.Invite, (role == Role.Owner ? Role.Admin : role).ToString(), by, lifetime ?? DefaultInviteLifetime, Math.Clamp(uses, 1, 1000), cancel);

    public async Task<RedeemResult> AcceptInvite(string? token, string? name, string? password, CancellationToken cancel) =>
        await Redeem(token, GrantKind.Invite, cancel, grant =>
            accounts.CreateAccount(name, password, Enum.TryParse<Role>(grant.Subject, out var role) ? role : Role.Member, cancel));

    public async Task<GrantToken?> CreateReset(string by, string user, CancellationToken cancel)
    {
        if (await store.Find(user, cancel) is null) return null;
        return await Create(GrantKind.Reset, user, by, ResetLifetime, 1, cancel);
    }

    public async Task<RedeemResult> Reset(string? token, string? password, CancellationToken cancel) =>
        await Redeem(token, GrantKind.Reset, cancel, async grant =>
        {
            if (!await accounts.SetPassword(grant.Subject, password, cancel)) return new(SignInStatus.Invalid);
            await accounts.RevokeAll(grant.Subject, cancel);
            return new(SignInStatus.Ok, grant.Subject);
        });

    /// <summary>What a link is for, to show the right page before anyone fills it in.</summary>
    public Task<Grant?> Peek(string? token, CancellationToken cancel) =>
        IsToken(token) ? grants.Find(AccountService.HashToken(token!), time.GetUtcNow(), cancel) : Task.FromResult<Grant?>(null);

    public Task<IReadOnlyList<Grant>> Invites(CancellationToken cancel) => grants.List(GrantKind.Invite, time.GetUtcNow(), cancel);

    public Task<bool> Delete(string id, CancellationToken cancel) => grants.Delete(id, cancel);

    private async Task<GrantToken> Create(GrantKind kind, string subject, string by, TimeSpan? lifetime, int uses, CancellationToken cancel)
    {
        var token = AccountService.NewToken();
        var now = time.GetUtcNow();
        var grant = new Grant(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), AccountService.HashToken(token), kind, subject, by, now, now + lifetime, uses);
        await grants.Create(grant, cancel);
        return new(token, grant);
    }

    /// <summary>
    /// Spends a use first, so two people racing for a single-use link cannot both win, runs the action,
    /// and gives the use back when the action failed, so a taken username does not burn an invite.
    /// </summary>
    private async Task<RedeemResult> Redeem(string? token, GrantKind kind, CancellationToken cancel, Func<Grant, Task<SignInResult>> action)
    {
        if (await Peek(token, cancel) is not { } grant || grant.Kind != kind) return new(RedeemStatus.Expired);
        if (!await grants.TryUse(grant.TokenHash, time.GetUtcNow(), cancel)) return new(RedeemStatus.Expired);
        var result = await action(grant);
        if (result.Status == SignInStatus.Ok) return new(RedeemStatus.Ok, result.Name);
        await grants.Refund(grant.TokenHash, cancel);
        return new(result.Status == SignInStatus.Taken ? RedeemStatus.Taken : RedeemStatus.Invalid);
    }

    private static bool IsToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server claimed by {User}")]
    private static partial void LogClaimed(ILogger logger, string user);
}

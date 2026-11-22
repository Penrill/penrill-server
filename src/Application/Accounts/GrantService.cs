using Microsoft.Extensions.Logging;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Models;

namespace Penrill.Application.Accounts;

public sealed record GrantToken(string Token, Grant Grant);

public enum RedeemStatus { Ok, Invalid, Expired, Taken }

public readonly record struct RedeemResult(RedeemStatus Status, string? User = null);

/// <summary>One-time links: the claim that makes a fresh server's owner, invites, and password resets.</summary>
public sealed partial class GrantService(
    IApplicationDbContext context, AccountService accounts, IIdentityService identity, TimeProvider time, ILogger<GrantService> logger)
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultInviteLifetime = TimeSpan.FromDays(7);

    /// <summary>True until someone claims the server and becomes its owner.</summary>
    public async Task<bool> Unclaimed() => !await identity.HasOwnerAsync();

    /// <summary>A new claim link; earlier ones stop working. Null when the server already has an owner.</summary>
    public async Task<string?> CreateClaim(CancellationToken cancellationToken)
    {
        if (!await Unclaimed()) return null;
        await context.Grants.Where(g => g.Kind == GrantKind.Claim).ExecuteDeleteAsync(cancellationToken);
        return (await Create(GrantKind.Claim, "", "", null, 1, cancellationToken)).Token;
    }

    public async Task<RedeemResult> Claim(string? token, string? name, string? password, CancellationToken cancellationToken)
    {
        if (!await Unclaimed()) return new(RedeemStatus.Expired);
        return await Redeem(token, GrantKind.Claim, cancellationToken, async _ =>
        {
            var created = await accounts.CreateAccount(name, password, Role.Owner);
            if (created.User is { } owner) LogClaimed(logger, owner);
            return created;
        });
    }

    public Task<GrantToken> CreateInvite(string by, Role role, TimeSpan? lifetime, int uses, CancellationToken cancellationToken) =>
        Create(GrantKind.Invite, (role == Role.Owner ? Role.Admin : role).ToString(), by, lifetime ?? DefaultInviteLifetime, Math.Clamp(uses, 1, 1000), cancellationToken);

    public Task<RedeemResult> AcceptInvite(string? token, string? name, string? password, CancellationToken cancellationToken) =>
        Redeem(token, GrantKind.Invite, cancellationToken, grant =>
            accounts.CreateAccount(name, password, Enum.TryParse<Role>(grant.Subject, out var role) ? role : Role.Member));

    public async Task<GrantToken?> CreateReset(string by, string user, CancellationToken cancellationToken)
    {
        if (await identity.FindAsync(user) is null) return null;
        return await Create(GrantKind.Reset, user, by, ResetLifetime, 1, cancellationToken);
    }

    public Task<RedeemResult> Reset(string? token, string? password, CancellationToken cancellationToken) =>
        Redeem(token, GrantKind.Reset, cancellationToken, async grant =>
        {
            if (!await accounts.SetPassword(grant.Subject, password)) return (Result.Failure([AccountErrors.Invalid]), null);
            await accounts.RevokeAll(grant.Subject, cancellationToken);
            return (Result.Success(), grant.Subject);
        });

    /// <summary>What a link is for, to show the right page before anyone fills it in.</summary>
    public async Task<Grant?> Peek(string? token, CancellationToken cancellationToken)
    {
        if (!IsToken(token)) return null;
        var hash = AccountService.HashToken(token!);
        var grant = await context.Grants.AsNoTracking().FirstOrDefaultAsync(g => g.TokenHash == hash, cancellationToken);
        return grant is not null && grant.IsValidAt(time.GetUtcNow()) ? grant : null;
    }

    /// <summary>Open invites, newest first. Used-up and expired links are cleared on the way.</summary>
    public async Task<IReadOnlyList<Grant>> Invites(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await context.Grants.Where(g => g.UsesLeft <= 0 || (g.Expires != null && g.Expires <= now)).ExecuteDeleteAsync(cancellationToken);
        var invites = await context.Grants.AsNoTracking().Where(g => g.Kind == GrantKind.Invite).ToListAsync(cancellationToken);
        return invites.OrderByDescending(g => g.Created).ToList();
    }

    public async Task<bool> Delete(string id, CancellationToken cancellationToken) =>
        await context.Grants.Where(g => g.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;

    private async Task<GrantToken> Create(GrantKind kind, string subject, string by, TimeSpan? lifetime, int uses, CancellationToken cancellationToken)
    {
        var token = AccountService.NewToken();
        var now = time.GetUtcNow();
        var grant = new Grant
        {
            Id = AccountService.NewId(), TokenHash = AccountService.HashToken(token), Kind = kind, Subject = subject, CreatedBy = by,
            Created = now, Expires = now + lifetime, UsesLeft = uses
        };
        context.Grants.Add(grant);
        await context.SaveChangesAsync(cancellationToken);
        return new(token, grant);
    }

    /// <summary>
    /// Spends a use first, so two people racing for a single-use link cannot both win, runs the action,
    /// and gives the use back when the action failed, so a taken username does not burn an invite.
    /// </summary>
    private async Task<RedeemResult> Redeem(string? token, GrantKind kind, CancellationToken cancellationToken, Func<Grant, Task<(Result Result, string? User)>> action)
    {
        if (await Peek(token, cancellationToken) is not { } grant || grant.Kind != kind) return new(RedeemStatus.Expired);
        var now = time.GetUtcNow();
        var spent = await context.Grants
            .Where(g => g.TokenHash == grant.TokenHash && g.UsesLeft > 0 && (g.Expires == null || g.Expires > now))
            .ExecuteUpdateAsync(set => set.SetProperty(g => g.UsesLeft, g => g.UsesLeft - 1), cancellationToken);
        if (spent != 1) return new(RedeemStatus.Expired);
        var (result, user) = await action(grant);
        if (result.Succeeded) return new(RedeemStatus.Ok, user);
        await context.Grants.Where(g => g.TokenHash == grant.TokenHash)
            .ExecuteUpdateAsync(set => set.SetProperty(g => g.UsesLeft, g => g.UsesLeft + 1), cancellationToken);
        return new(result.Errors.Contains(AccountErrors.Taken) ? RedeemStatus.Taken : RedeemStatus.Invalid);
    }

    private static bool IsToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server claimed by {User}")]
    private static partial void LogClaimed(ILogger logger, string user);
}

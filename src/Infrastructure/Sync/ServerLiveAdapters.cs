using Application.Accounts;
using Application.Boards;
using Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Sync;

public sealed class ServerLiveAuthenticator(IServiceScopeFactory scopes) : ILiveAuthenticator
{
    public async Task<LiveIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<AccountService>().Authenticate(token, cancellationToken);
        return session is null
            ? null
            : new LiveIdentity(session.User, session.User, session.CredentialId, session.Expires, session.Role == Role.Guest);
    }
}

public sealed class ServerLiveAccessPolicy(IServiceScopeFactory scopes) : ILiveAccessPolicy
{
    public async Task<BoardAccessGrant> EvaluateAsync(LiveIdentity identity, string boardId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var access = await context.AccessAsync(identity.UserId, boardId, cancellationToken);
        return access switch
        {
            BoardAccess.Missing => BoardAccessGrant.Missing,
            BoardAccess.Denied => BoardAccessGrant.Denied,
            _ => new BoardAccessGrant(BoardAccessStatus.Allowed, BoardPermission.Edit, BoardLimitPolicy.SelfHostedDefault)
        };
    }
}

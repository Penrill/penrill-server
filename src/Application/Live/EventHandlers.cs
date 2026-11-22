using Microsoft.Extensions.DependencyInjection;
using Penrill.Application.Boards;
using Penrill.Application.Common.Interfaces;

namespace Penrill.Application.Live;

/// <summary>Closes the live connections of a credential that stopped being valid.</summary>
public sealed class CloseRevokedConnections(LiveConnections live) : INotificationHandler<CredentialRevokedEvent>
{
    public Task Handle(CredentialRevokedEvent notification, CancellationToken cancellationToken)
    {
        live.CloseCredential(notification.CredentialId);
        return Task.CompletedTask;
    }
}

/// <summary>Unloads a deleted board's room and disconnects everyone on it.</summary>
public sealed class ForgetDeletedBoard(RoomRegistry rooms) : INotificationHandler<BoardDeletedEvent>
{
    public Task Handle(BoardDeletedEvent notification, CancellationToken cancellationToken) => rooms.Forget(notification.BoardId);
}

/// <summary>
/// Access is checked once when a live connection opens, so after someone may have lost it this ends the
/// connections of whoever is on the board's open room and may no longer use it.
/// </summary>
public sealed class RecheckBoardAccess(RoomRegistry rooms, IServiceScopeFactory scopes) : INotificationHandler<BoardAccessRevokedEvent>
{
    public async Task Handle(BoardAccessRevokedEvent notification, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        await rooms.Recheck(notification.BoardId,
            async (user, cancel) => await context.AccessAsync(user, notification.BoardId, cancel) == BoardAccess.Allowed, cancellationToken);
    }
}

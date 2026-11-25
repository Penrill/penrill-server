using Application.Accounts;
using Application.Boards;
using Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Live;

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

/// <summary>Disconnects live room members who lost access to a board.</summary>
public sealed class RecheckBoardAccess(RoomRegistry rooms, IServiceScopeFactory scopes) : INotificationHandler<BoardAccessRevokedEvent>
{
    public async Task Handle(BoardAccessRevokedEvent notification, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        await rooms.Recheck(notification.BoardId,
            async (userId, cancel) => await context.AccessAsync(userId, notification.BoardId, cancel) == BoardAccess.Allowed, cancellationToken);
    }
}

/// <summary>Deletes expired bearer sessions once an hour.</summary>
public sealed partial class ExpiredSessionCleaner(IServiceScopeFactory scopes, TimeProvider time, ILogger<ExpiredSessionCleaner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AccountService>().DeleteExpiredSessions(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    LogSweepFailed(logger, error);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Expired session cleanup failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception error);
}

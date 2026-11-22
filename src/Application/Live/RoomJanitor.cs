using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Penrill.Application.Accounts;

namespace Penrill.Application.Live;

/// <summary>Unloads idle board rooms every 30 seconds and deletes expired sessions once an hour; closes every room on shutdown.</summary>
public sealed partial class RoomJanitor(RoomRegistry rooms, IServiceScopeFactory scopes, TimeProvider time, ILogger<RoomJanitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly int SessionSweepEvery = (int)(TimeSpan.FromHours(1) / Interval);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        var ticks = 0;
        try
        {
            do
            {
                try
                {
                    await rooms.EvictIdle();
                    if (ticks++ % SessionSweepEvery == 0)
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<AccountService>().DeleteExpiredSessions(stoppingToken);
                    }
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await rooms.CloseAll();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Housekeeping failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception error);
}

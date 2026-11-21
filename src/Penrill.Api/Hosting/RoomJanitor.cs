using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Penrill.Core.Accounts;
using Penrill.Core.Rooms;

namespace Penrill.Api.Hosting;

internal sealed partial class RoomJanitor(RoomRegistry rooms, AccountService accounts, TimeProvider time, ILogger<RoomJanitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly int SessionSweepEvery = (int)(TimeSpan.FromHours(1) / Interval);

    protected override async Task ExecuteAsync(CancellationToken stopping)
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
                    if (ticks++ % SessionSweepEvery == 0) await accounts.DeleteExpiredSessions(stopping);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    LogSweepFailed(logger, error);
                }
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancel)
    {
        await base.StopAsync(cancel);
        await rooms.CloseAll();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Housekeeping failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception error);
}

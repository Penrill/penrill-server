using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sync.Live;

/// <summary>Unloads idle board rooms every 30 seconds and closes every room on shutdown.</summary>
public sealed partial class RoomJanitor(RoomRegistry rooms, TimeProvider time, ILogger<RoomJanitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await rooms.EvictIdle();
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Room housekeeping failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception error);
}

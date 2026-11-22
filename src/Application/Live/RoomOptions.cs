namespace Penrill.Application.Live;

public sealed record RoomOptions
{
    public int SnapshotEvery { get; init; } = 500;
    public int RecentChanges { get; init; } = 1000;
    public long RecentBytes { get; init; } = 16 * 1024 * 1024;
    public TimeSpan ReceiptRetention { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

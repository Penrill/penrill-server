using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sync.Live;

public static class SyncTelemetry
{
    public const string Name = "Penrill";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> ChangesAccepted =
        Meter.CreateCounter<long>("penrill.changes.accepted", "{change}", "Changes appended to a board log");

    public static readonly Counter<long> ChangesDuplicate =
        Meter.CreateCounter<long>("penrill.changes.duplicate", "{change}", "Changes sent again after the server had accepted them");

    public static readonly Counter<long> ChangesRejected =
        Meter.CreateCounter<long>("penrill.changes.rejected", "{change}", "Changes refused for good, by reason");

    public static readonly Histogram<double> SubmitDuration =
        Meter.CreateHistogram<double>("penrill.change.duration", "ms", "Time to order, store and broadcast one change");

    public static readonly Histogram<double> LoadDuration =
        Meter.CreateHistogram<double>("penrill.board.load.duration", "ms", "Time to load a board into memory");

    public static readonly UpDownCounter<long> ActiveRooms =
        Meter.CreateUpDownCounter<long>("penrill.rooms.active", "{board}", "Boards loaded in memory");

    public static readonly Histogram<double> SnapshotDuration =
        Meter.CreateHistogram<double>("penrill.snapshot.duration", "ms", "Time to write a board snapshot");

    public static readonly Histogram<long> SnapshotSize =
        Meter.CreateHistogram<long>("penrill.snapshot.size", "By", "Size of written board snapshots");

    public static readonly Counter<long> CatchUps =
        Meter.CreateCounter<long>("penrill.live.catchups", "{connection}", "How joining clients caught up: current, delta or snapshot");

    public static readonly UpDownCounter<long> LiveConnections =
        Meter.CreateUpDownCounter<long>("penrill.live.connections", "{connection}", "Open live board connections");

    public static readonly Counter<long> LiveClosed =
        Meter.CreateCounter<long>("penrill.live.closed", "{connection}", "Live connections closed by the server, by reason");

    public static readonly Counter<long> AssetBytes =
        Meter.CreateCounter<long>("penrill.assets.uploaded", "By", "Image bytes uploaded");

    public static readonly Counter<long> AuthFailures =
        Meter.CreateCounter<long>("penrill.auth.failures", "{attempt}", "Refused sign-ins and session tokens, by kind");

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}

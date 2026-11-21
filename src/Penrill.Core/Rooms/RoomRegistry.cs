using Microsoft.Extensions.Logging;
using Penrill.Core.Boards;

namespace Penrill.Core.Rooms;

public sealed class RoomRegistry(IBoardStore store, RoomOptions options, TimeProvider time, ILoggerFactory loggers)
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, Entry> rooms = new(StringComparer.Ordinal);
    private readonly ILogger logger = loggers.CreateLogger<BoardRoom>();

    public int Count
    {
        get { lock (sync) return rooms.Count; }
    }

    public async Task<RoomLease?> Acquire(string boardId, CancellationToken cancel)
    {
        Entry entry;
        lock (sync)
        {
            if (!rooms.TryGetValue(boardId, out entry!))
                rooms[boardId] = entry = new Entry(Task.Run(() => BoardRoom.Load(boardId, store, options, time, logger)));
            entry.Leases++;
        }
        try
        {
            if (await entry.Room.WaitAsync(cancel) is { } room) return new RoomLease(this, boardId, entry, room);
        }
        catch
        {
            Release(boardId, entry, forget: entry.Room.IsFaulted);
            throw;
        }
        Release(boardId, entry, forget: true);
        return null;
    }

    public async Task EvictIdle()
    {
        List<BoardRoom> idle = [];
        lock (sync)
        {
            foreach (var (id, entry) in rooms.ToArray())
            {
                if (entry.Leases > 0 || !entry.Room.IsCompletedSuccessfully || time.GetElapsedTime(entry.IdleSince) < options.IdleTimeout) continue;
                rooms.Remove(id);
                if (entry.Room.Result is { } room) idle.Add(room);
            }
        }
        foreach (var room in idle) await room.Close();
    }

    public async Task CloseAll()
    {
        BoardRoom[] open;
        lock (sync)
        {
            open = [.. rooms.Values.Where(entry => entry.Room.IsCompletedSuccessfully && entry.Room.Result is not null).Select(entry => entry.Room.Result!)];
            rooms.Clear();
        }
        foreach (var room in open) await room.Close();
    }

    private void Release(string boardId, Entry entry, bool forget)
    {
        lock (sync)
        {
            entry.Leases--;
            if (entry.Leases > 0) return;
            entry.IdleSince = time.GetTimestamp();
            if (forget && rooms.TryGetValue(boardId, out var current) && current == entry) rooms.Remove(boardId);
        }
    }

    internal sealed class Entry(Task<BoardRoom?> room)
    {
        public readonly Task<BoardRoom?> Room = room;
        public int Leases;
        public long IdleSince;
    }

    public sealed class RoomLease : IDisposable
    {
        private readonly RoomRegistry registry;
        private readonly string boardId;
        private readonly Entry entry;
        private int released;

        internal RoomLease(RoomRegistry registry, string boardId, Entry entry, BoardRoom room)
        {
            this.registry = registry;
            this.boardId = boardId;
            this.entry = entry;
            Room = room;
        }

        public BoardRoom Room { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) registry.Release(boardId, entry, forget: false);
        }
    }
}

using Microsoft.Extensions.Logging;
using Penrill.Domain.Boards;

namespace Penrill.Application.Live;

public sealed class RoomRegistry(BoardLog store, RoomOptions options, TimeProvider time, ILoggerFactory loggers)
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

    /// <summary>
    /// After a board was deleted: forgets its room and disconnects everyone on it. Changes still in
    /// flight fail, since the store no longer has the board.
    /// </summary>
    public async Task Forget(string boardId)
    {
        Entry? entry;
        lock (sync)
        {
            if (!rooms.Remove(boardId, out entry)) return;
        }
        BoardRoom? room;
        try
        {
            room = await entry.Room;
        }
        catch
        {
            return; // The room never loaded: nobody is on it.
        }
        if (room is not null) await room.Close(LiveCloseReason.NotFound);
    }

    /// <summary>
    /// After people lost access to a board (taken off it, or its draw link turned off): disconnects whoever is on its
    /// open room and may no longer use it. Access is checked once when a socket connects, so this is what ends the ones already open.
    /// </summary>
    public async Task Recheck(string boardId, Func<string, CancellationToken, Task<bool>> mayStay, CancellationToken cancel)
    {
        Entry? entry;
        lock (sync)
        {
            if (!rooms.TryGetValue(boardId, out entry)) return;
        }
        if (!entry.Room.IsCompletedSuccessfully || entry.Room.Result is not { } room) return;
        foreach (var member in room.Members())
            if (!await mayStay(member.User, cancel)) member.Close(LiveCloseReason.Forbidden);
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

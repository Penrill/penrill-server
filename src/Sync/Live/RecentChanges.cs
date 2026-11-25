namespace Sync.Live;

internal sealed class RecentChanges(int capacity, long maxBytes)
{
    private readonly Queue<StoredChange> entries = new();
    private readonly Dictionary<string, long> ids = new(StringComparer.Ordinal);
    private long bytes;

    public void Add(StoredChange change)
    {
        entries.Enqueue(change);
        ids[change.Id] = change.Seq;
        bytes += change.Body.Length;
        while (entries.Count > 1 && (entries.Count > capacity || bytes > maxBytes))
        {
            var old = entries.Dequeue();
            ids.Remove(old.Id);
            bytes -= old.Body.Length;
        }
    }

    public bool TryFind(string id, out long seq) => ids.TryGetValue(id, out seq);

    public bool Covers(long after) => entries.Count > 0 && after >= entries.Peek().Seq - 1;

    public IEnumerable<StoredChange> After(long after) => entries.Where(change => change.Seq > after);
}

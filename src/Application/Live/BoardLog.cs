using Microsoft.Extensions.DependencyInjection;
using Penrill.Application.Common.Interfaces;

namespace Penrill.Application.Live;

public sealed record StoredChange(long Seq, string Id, string Author, byte[] Body);

public sealed record StoredBoard(
    string Id, string Title, string Background, long Seq, long SnapshotSeq, byte[] Items, IReadOnlyList<StoredChange> Recent);

public sealed record AppendRequest(
    string Board, long Seq, string ChangeId, string Author, byte[] Body, string Title, string Background, DateTimeOffset At);

public readonly record struct AppendOutcome(bool Duplicate, long Seq);

public sealed record SnapshotRecord(string Board, long Seq, byte[] Items, DateTimeOffset At);

/// <summary>
/// The persistent side of a board room: loads a board from its snapshot and log, appends accepted changes and
/// writes snapshots. Rooms outlive requests, so every call runs in a scope of its own.
/// </summary>
public sealed class BoardLog(IServiceScopeFactory scopes)
{
    public async Task<StoredBoard?> Load(string board, int recent, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var head = await context.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == board, cancellationToken);
        if (head is null) return null;
        var from = Math.Min(head.SnapshotSeq, head.Seq - recent);
        var changes = await context.BoardChanges.AsNoTracking()
            .Where(c => c.BoardId == board && c.Body != null && c.Seq > from)
            .OrderBy(c => c.Seq)
            .Select(c => new StoredChange(c.Seq, c.ChangeId, c.Author, c.Body!))
            .ToListAsync(cancellationToken);
        return new StoredBoard(board, head.Title, head.Background, head.Seq, head.SnapshotSeq, head.Snapshot, changes);
    }

    /// <summary>
    /// Stores the change as number <see cref="AppendRequest.Seq"/>. A change id the board already has is a duplicate:
    /// its original sequence comes back and nothing is written. Throws when the board is not at the sequence before.
    /// </summary>
    public async Task<AppendOutcome> Append(AppendRequest request, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.BoardChanges
            .Where(c => c.BoardId == request.Board && c.ChangeId == request.ChangeId)
            .Select(c => (long?)c.Seq)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } seq) return new AppendOutcome(true, seq);
        context.BoardChanges.Add(new BoardChange
        {
            BoardId = request.Board, Seq = request.Seq, ChangeId = request.ChangeId, Author = request.Author, At = request.At, Body = request.Body
        });
        await context.SaveChangesAsync(cancellationToken);
        var moved = await context.Boards
            .Where(b => b.Id == request.Board && b.Seq == request.Seq - 1)
            .ExecuteUpdateAsync(set => set
                .SetProperty(b => b.Seq, request.Seq)
                .SetProperty(b => b.Title, request.Title)
                .SetProperty(b => b.Background, request.Background)
                .SetProperty(b => b.Updated, request.At), cancellationToken);
        if (moved != 1) throw new InvalidOperationException($"Board {request.Board} is not at seq {request.Seq - 1}.");
        await transaction.CommitAsync(cancellationToken);
        return new AppendOutcome(false, request.Seq);
    }

    /// <summary>
    /// Writes the snapshot, then drops change bodies it covers beyond the last <paramref name="retainBodies"/>,
    /// and the receipts of those older than <paramref name="retainReceipts"/>.
    /// </summary>
    public async Task SaveSnapshot(SnapshotRecord snapshot, int retainBodies, TimeSpan retainReceipts, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Boards
            .Where(b => b.Id == snapshot.Board && b.SnapshotSeq < snapshot.Seq)
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.Snapshot, snapshot.Items).SetProperty(b => b.SnapshotSeq, snapshot.Seq), cancellationToken);
        var compacted = snapshot.Seq - retainBodies;
        await context.BoardChanges
            .Where(c => c.BoardId == snapshot.Board && c.Seq <= compacted && c.Body != null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Body, (byte[]?)null), cancellationToken);
        var cutoff = snapshot.At - retainReceipts;
        await context.BoardChanges
            .Where(c => c.BoardId == snapshot.Board && c.Seq <= compacted && c.At < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

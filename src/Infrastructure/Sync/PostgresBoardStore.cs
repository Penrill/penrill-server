using Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Sync;

/// <summary>PostgreSQL backing store for shared <see cref="BoardRoom"/> instances.</summary>
public sealed class PostgresBoardStore(IServiceScopeFactory scopes) : IBoardStore
{
    public async Task<StoredBoard?> LoadAsync(string boardId, int recentChanges, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var head = await context.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, cancellationToken);
        if (head is null) return null;
        var from = Math.Min(head.SnapshotSeq, head.Seq - recentChanges);
        var changes = await context.BoardChanges.AsNoTracking()
            .Where(c => c.BoardId == boardId && c.Body != null && c.Seq > from)
            .OrderBy(c => c.Seq)
            .Select(c => new StoredChange(c.Seq, c.ChangeId, c.Author, c.Body!))
            .ToListAsync(cancellationToken);
        return new StoredBoard(boardId, head.Title, head.Background, head.Seq, head.SnapshotSeq, head.Snapshot, changes);
    }

    public async Task<AppendOutcome> AppendAsync(AppendRequest request, CancellationToken cancellationToken)
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
            BoardId = request.Board,
            Seq = request.Seq,
            ChangeId = request.ChangeId,
            Author = request.Author,
            At = request.At,
            Body = request.Body
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

    public async Task SaveSnapshotAsync(SnapshotRecord snapshot, int retainBodies, TimeSpan retainReceipts, CancellationToken cancellationToken)
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

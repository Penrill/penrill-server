using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Penrill.Domain.Common;

namespace Penrill.Infrastructure.Data.Interceptors;

/// <summary>
/// Publishes the domain events of saved entities once the save succeeded, so handlers see the new state
/// (a connection closed for lost access is checked against what was just committed).
/// </summary>
public class DispatchDomainEventsInterceptor(IPublisher publisher) : SaveChangesInterceptor
{
    private readonly List<BaseEvent> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PublishAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await PublishAsync(cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _pending.Clear();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context == null) return;
        var entities = context.ChangeTracker.Entries<BaseEntity>().Where(e => e.Entity.DomainEvents.Count != 0).Select(e => e.Entity).ToList();
        _pending.AddRange(entities.SelectMany(e => e.DomainEvents));
        entities.ForEach(e => e.ClearDomainEvents());
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var events = _pending.ToList();
        _pending.Clear();
        foreach (var domainEvent in events) await publisher.Publish(domainEvent, cancellationToken);
    }
}

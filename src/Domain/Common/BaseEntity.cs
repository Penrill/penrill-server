using System.ComponentModel.DataAnnotations.Schema;

namespace Domain.Common;

/// <summary>
/// An entity that can raise domain events. Keys differ per entity (board ids are hex strings,
/// change rows are keyed by board and sequence), so each entity declares its own.
/// </summary>
public abstract class BaseEntity
{
    private readonly List<BaseEvent> _domainEvents = [];

    [NotMapped]
    public IReadOnlyCollection<BaseEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(BaseEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void RemoveDomainEvent(BaseEvent domainEvent) => _domainEvents.Remove(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

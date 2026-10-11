namespace RetailFlow.Domain.Platform;

/// <summary>
/// Consistency boundary: an aggregate is loaded, validated and saved as one unit, and it collects
/// the domain events raised while it changed. The persistence layer drains those events into the
/// transactional outbox in the same transaction as the state change (ADR-0011).
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>Optimistic concurrency token, mapped to the provider's row-version column.</summary>
    public byte[]? ConcurrencyToken { get; private set; }

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>Called by the persistence layer once the events have been written to the outbox.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}

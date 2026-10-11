namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>A business action worth recording for later review (FR-SEC-11, FR-TEN-07).</summary>
/// <param name="Action">Dotted action code, e.g. <c>sales.void</c> or <c>identity.role_changed</c>.</param>
/// <param name="EntityType">The entity affected, e.g. <c>Sale</c>.</param>
/// <param name="EntityId">The affected record, when there is a single one.</param>
/// <param name="Before">State before the change, already stripped of secrets.</param>
/// <param name="After">State after the change, already stripped of secrets.</param>
/// <param name="Metadata">Extra context such as reason, approver or threshold exceeded.</param>
public sealed record AuditEntry(
    string Action,
    string EntityType,
    Guid? EntityId = null,
    object? Before = null,
    object? After = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Writes audit records. Entries written during a business transaction commit with it, so there is
/// never an audit trail for a change that rolled back, nor a change without its trail.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes domain events. The implementation writes them to the transactional outbox, so
/// publication is atomic with the state change that produced them (ADR-0011).
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(Domain.Platform.IDomainEvent domainEvent, CancellationToken cancellationToken = default);

    Task PublishAsync(
        IEnumerable<Domain.Platform.IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default);
}

namespace RetailFlow.Domain.Platform;

/// <summary>
/// A record owned by exactly one tenant. Every such entity is covered by the EF global query
/// filter and by the tenant guard in the save pipeline (ADR-0004).
/// </summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; }

    /// <summary>
    /// Assigns the owning organization. Called by the persistence layer on insert and by factory
    /// methods; it throws once a different organization is already set, so a record can never be
    /// moved between tenants.
    /// </summary>
    void AssignOrganization(Guid organizationId);
}

/// <summary>Carries who created and last changed a record, and when (UTC).</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; }

    Guid? CreatedByUserId { get; }

    DateTimeOffset? UpdatedAtUtc { get; }

    Guid? UpdatedByUserId { get; }

    void MarkCreated(DateTimeOffset atUtc, Guid? userId);

    void MarkUpdated(DateTimeOffset atUtc, Guid? userId);
}

/// <summary>A record that is withdrawn rather than deleted, so history stays intact.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAtUtc { get; }

    Guid? DeletedByUserId { get; }

    void MarkDeleted(DateTimeOffset atUtc, Guid? userId);

    void Restore();
}

/// <summary>
/// A record that offline clients pull incrementally. The sequence is assigned per tenant by the
/// persistence layer on every insert and update (ADR-0018).
/// </summary>
public interface IHasChangeSequence
{
    long ChangeSequence { get; }

    void AssignChangeSequence(long sequence);
}

/// <summary>Something that happened in the domain and that other modules may react to.</summary>
public interface IDomainEvent
{
    /// <summary>Stable identifier of this occurrence, reused as the outbox message id.</summary>
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Wire name used by the outbox, e.g. <c>sales.sale-completed</c>.</summary>
    string EventType { get; }
}

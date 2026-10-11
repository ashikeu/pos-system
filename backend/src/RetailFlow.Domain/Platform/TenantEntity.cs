namespace RetailFlow.Domain.Platform;

/// <summary>
/// An aggregate that records who changed it and when, and that offline clients pull incrementally
/// (ADR-0007, ADR-0018). The setters are deliberately not public: only the persistence pipeline
/// stamps these values, so they cannot be forged by a caller.
/// </summary>
public abstract class AuditableAggregateRoot : AggregateRoot, IAuditable, IHasChangeSequence
{
    protected AuditableAggregateRoot(Guid id)
        : base(id)
    {
    }

    protected AuditableAggregateRoot()
    {
    }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public long ChangeSequence { get; private set; }

    public void MarkCreated(DateTimeOffset atUtc, Guid? userId)
    {
        CreatedAtUtc = atUtc;
        CreatedByUserId = userId;
    }

    public void MarkUpdated(DateTimeOffset atUtc, Guid? userId)
    {
        UpdatedAtUtc = atUtc;
        UpdatedByUserId = userId;
    }

    public void AssignChangeSequence(long sequence)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Change sequence must be positive.");
        }

        ChangeSequence = sequence;
    }
}

/// <summary>
/// Base type for tenant-owned aggregates. Carrying the organization here means every module gets
/// the global query filter, the tenant stamp and the cross-tenant guard for free (ADR-0004).
/// </summary>
public abstract class TenantEntity : AuditableAggregateRoot, ITenantOwned
{
    protected TenantEntity(Guid id, Guid organizationId)
        : base(id)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        OrganizationId = organizationId;
    }

    protected TenantEntity()
    {
    }

    public Guid OrganizationId { get; private set; }

    public void AssignOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (OrganizationId != Guid.Empty && OrganizationId != organizationId)
        {
            throw new TenantMismatchException(OrganizationId, organizationId, GetType().Name, Id);
        }

        OrganizationId = organizationId;
    }
}

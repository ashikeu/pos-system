namespace RetailFlow.Domain.Platform;

/// <summary>
/// Raised when a domain invariant is violated, that is, when code attempted something the model
/// forbids. These are defects, not user errors: expected business failures are returned as
/// <see cref="Result"/> values instead.
/// </summary>
public class DomainException : Exception
{
    public DomainException()
        : base("A domain invariant was violated.")
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a record is about to be written or read under the wrong organization. This is the
/// last line of tenant isolation (ADR-0004) and always indicates a defect, so it fails loudly.
/// </summary>
public sealed class TenantMismatchException : DomainException
{
    public TenantMismatchException()
        : base("A record was accessed under the wrong organization.")
    {
    }

    public TenantMismatchException(string message)
        : base(message)
    {
    }

    public TenantMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TenantMismatchException(Guid ownerOrganizationId, Guid ambientOrganizationId, string entityType, Guid entityId)
        : base($"{entityType} {entityId} belongs to organization {ownerOrganizationId} but was used under {ambientOrganizationId}.")
    {
        OwnerOrganizationId = ownerOrganizationId;
        AmbientOrganizationId = ambientOrganizationId;
        EntityType = entityType;
        EntityId = entityId;
    }

    /// <summary>The organization the record actually belongs to.</summary>
    public Guid OwnerOrganizationId { get; }

    /// <summary>The organization in scope for the current operation.</summary>
    public Guid AmbientOrganizationId { get; }

    public string? EntityType { get; }

    public Guid EntityId { get; }
}

/// <summary>Raised when an operation requires tenant context that was never established.</summary>
public sealed class MissingTenantContextException : DomainException
{
    public MissingTenantContextException()
        : base("The operation requires an organization context, but none was established for this request.")
    {
    }

    public MissingTenantContextException(string message)
        : base(message)
    {
    }

    public MissingTenantContextException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

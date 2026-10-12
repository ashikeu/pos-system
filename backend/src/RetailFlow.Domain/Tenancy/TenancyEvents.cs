using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>
/// Raised when a tenant is blocked (FR-TEN-06, FR-TEN-07). Consumers notify the owner and stop
/// scheduled work for that tenant; the read-only gate itself does not wait for the event, it reads
/// the organization's status.
/// </summary>
/// <param name="EventId">Identifier of this occurrence.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="Code">Its code, so a consumer needs no second lookup.</param>
/// <param name="Reason">Why it was suspended.</param>
public sealed record OrganizationSuspended(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid OrganizationId,
    string Code,
    string Reason) : IDomainEvent
{
    public string EventType => "tenancy.organization-suspended";
}

/// <summary>Raised when a suspended tenant is allowed to trade again.</summary>
/// <param name="EventId">Identifier of this occurrence.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="Code">Its code.</param>
public sealed record OrganizationReactivated(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid OrganizationId,
    string Code) : IDomainEvent
{
    public string EventType => "tenancy.organization-reactivated";
}

/// <summary>
/// Raised when a tenant is closed for good. The data is retained for the statutory period before
/// purge (NFR-09), so consumers schedule rather than delete.
/// </summary>
/// <param name="EventId">Identifier of this occurrence.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="Code">Its code.</param>
/// <param name="Reason">Why it was closed.</param>
public sealed record OrganizationClosed(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid OrganizationId,
    string Code,
    string Reason) : IDomainEvent
{
    public string EventType => "tenancy.organization-closed";
}

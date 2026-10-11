using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>The tenant, branch and terminal a request acts within.</summary>
/// <param name="OrganizationId">The owning organization. Always set for tenant requests.</param>
/// <param name="BranchId">The branch the caller selected, validated against their grants.</param>
/// <param name="TerminalId">The POS terminal, when the caller is a terminal.</param>
/// <param name="IsPlatformAdmin">
/// True only for platform operators acting through explicitly marked, audited endpoints
/// (ADR-0004). It never relaxes the tenant filter implicitly.
/// </param>
public sealed record TenantScope(
    Guid OrganizationId,
    Guid? BranchId = null,
    Guid? TerminalId = null,
    bool IsPlatformAdmin = false);

/// <summary>
/// Ambient tenant context for the current request or job. Resolved from the access token, never
/// from a request body or query parameter (ADR-0004).
/// </summary>
public interface ITenantContext
{
    /// <summary>The current organization, or <c>null</c> when no tenant has been established.</summary>
    Guid? OrganizationIdOrNull { get; }

    /// <summary>
    /// The current organization. Throws <see cref="MissingTenantContextException"/> when none is
    /// set, because silently returning <see cref="Guid.Empty"/> would widen a query to no tenant
    /// at all — a data leak waiting to happen.
    /// </summary>
    Guid OrganizationId { get; }

    Guid? BranchId { get; }

    Guid? TerminalId { get; }

    bool IsPlatformAdmin { get; }

    bool HasTenant { get; }
}

/// <summary>
/// Write side of <see cref="ITenantContext"/>, used by the authentication middleware, background
/// jobs that process one tenant at a time, and integration tests. Not injected into handlers.
/// </summary>
public interface IMutableTenantContext : ITenantContext
{
    void Set(TenantScope scope);

    void Clear();
}

/// <summary>The authenticated principal behind the current request.</summary>
/// <param name="UserId">The acting user, or <c>null</c> for a system/background actor.</param>
/// <param name="UserName">Display name for audit records.</param>
/// <param name="PermissionsVersion">
/// Version of the user's permission set; a bump invalidates cached permissions immediately
/// (ADR-0012).
/// </param>
/// <param name="DeviceId">Registered device, when the call comes from a POS terminal.</param>
public sealed record UserScope(
    Guid? UserId,
    string? UserName = null,
    int PermissionsVersion = 0,
    Guid? DeviceId = null);

/// <summary>Who is acting, for authorization checks and audit stamping.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? UserName { get; }

    int PermissionsVersion { get; }

    Guid? DeviceId { get; }

    bool IsAuthenticated { get; }
}

/// <summary>Write side of <see cref="ICurrentUser"/>. Set by middleware, jobs and tests only.</summary>
public interface IMutableCurrentUser : ICurrentUser
{
    void Set(UserScope scope);

    void Clear();
}

/// <summary>
/// Correlation identifier threaded through logs, audit records and outbox messages so one customer
/// complaint can be traced across the API, the workers and the device that sent it (ADR-0014).
/// </summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

/// <summary>Write side of <see cref="ICorrelationContext"/>.</summary>
public interface IMutableCorrelationContext : ICorrelationContext
{
    void Set(string correlationId);
}

/// <summary>
/// Transport details of the current call, recorded on audit rows, sessions and login history.
/// Supplied by the API layer; background jobs leave it empty.
/// </summary>
public interface IRequestMetadata
{
    string? IpAddress { get; }

    string? UserAgent { get; }
}

/// <summary>Write side of <see cref="IRequestMetadata"/>, set by API middleware.</summary>
public interface IMutableRequestMetadata : IRequestMetadata
{
    void Set(string? ipAddress, string? userAgent);
}

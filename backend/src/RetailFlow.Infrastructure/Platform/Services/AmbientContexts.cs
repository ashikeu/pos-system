using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Infrastructure.Platform.Services;

/// <summary>The real clock. Always UTC (ADR-0007).</summary>
internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Identifier generator using the UUIDv7 policy (ADR-0005).</summary>
internal sealed class UuidV7Generator : IGuidGenerator
{
    public Guid NewId() => UuidV7.NewGuid();

    public Guid NewId(DateTimeOffset timestamp) => UuidV7.NewGuid(timestamp);
}

/// <summary>
/// Per-request tenant scope. Scoped, so one request or one job iteration sees exactly one tenant;
/// it is set from the access token by middleware, never from request content (ADR-0004).
/// </summary>
internal sealed class AmbientTenantContext : IMutableTenantContext
{
    private TenantScope? _scope;

    public Guid? OrganizationIdOrNull => _scope?.OrganizationId;

    public Guid OrganizationId => _scope?.OrganizationId
        ?? throw new MissingTenantContextException();

    public Guid? BranchId => _scope?.BranchId;

    public Guid? TerminalId => _scope?.TerminalId;

    public bool IsPlatformAdmin => _scope?.IsPlatformAdmin ?? false;

    public bool HasTenant => _scope is not null;

    public void Set(TenantScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(scope));
        }

        _scope = scope;
    }

    public void Clear() => _scope = null;
}

/// <summary>Per-request principal, set by the authentication middleware or a job.</summary>
internal sealed class AmbientCurrentUser : IMutableCurrentUser
{
    private UserScope? _scope;

    public Guid? UserId => _scope?.UserId;

    public string? UserName => _scope?.UserName;

    public int PermissionsVersion => _scope?.PermissionsVersion ?? 0;

    public Guid? DeviceId => _scope?.DeviceId;

    public bool IsAuthenticated => _scope?.UserId is not null;

    public void Set(UserScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
    }

    public void Clear() => _scope = null;
}

/// <summary>
/// Per-request correlation id. Defaults to a fresh value so a background job or a direct call
/// still produces traceable logs and audit records.
/// </summary>
internal sealed class AmbientCorrelationContext : IMutableCorrelationContext
{
    private string? _correlationId;

    public string CorrelationId => _correlationId ??= Guid.NewGuid().ToString("N");

    public void Set(string correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            // Trimmed and bounded: this value arrives in a header and ends up in logs, audit rows
            // and outbox messages, so it must not be unbounded client input.
            var trimmed = correlationId.Trim();
            _correlationId = trimmed.Length > 64 ? trimmed[..64] : trimmed;
        }
    }
}

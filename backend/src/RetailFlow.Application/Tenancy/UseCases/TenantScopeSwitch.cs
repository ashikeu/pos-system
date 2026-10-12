using RetailFlow.Application.Platform.Abstractions;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>
/// Temporarily makes another organization the ambient tenant, and puts the previous scope back on
/// dispose. Only two kinds of operation need this: provisioning, which creates the tenant it writes
/// for, and platform-operator actions on a named tenant.
/// </summary>
/// <remarks>
/// The save pipeline checks every tenant-owned row against the ambient tenant and makes no
/// exception for operators (ADR-0004), so a handler that writes another tenant's rows must both
/// switch the scope <em>and</em> flush inside it — the guard runs at <c>SaveChanges</c>, not where
/// the entity was changed.
/// </remarks>
internal sealed class TenantScopeSwitch : IDisposable
{
    private readonly IMutableTenantContext _mutable;
    private readonly TenantScope? _previous;
    private bool _restored;

    private TenantScopeSwitch(IMutableTenantContext mutable, TenantScope? previous)
    {
        _mutable = mutable;
        _previous = previous;
    }

    public static TenantScopeSwitch To(
        IMutableTenantContext mutable,
        ITenantContext current,
        Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(mutable);
        ArgumentNullException.ThrowIfNull(current);

        var previous = current.OrganizationIdOrNull is { } existing
            ? new TenantScope(existing, current.BranchId, current.TerminalId, current.IsPlatformAdmin)
            : null;

        mutable.Set(new TenantScope(organizationId, IsPlatformAdmin: previous?.IsPlatformAdmin ?? false));

        return new TenantScopeSwitch(mutable, previous);
    }

    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;

        if (_previous is null)
        {
            _mutable.Clear();
        }
        else
        {
            _mutable.Set(_previous);
        }
    }
}

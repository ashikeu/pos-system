using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Tenancy.Persistence;

/// <summary>
/// Tenancy data access. Reads that take an organization id explicitly bypass the query filter and
/// compare the column instead; the id always comes from the token or from a platform-operator
/// endpoint, never from a request body, so the scope is still server-decided (ADR-0004).
/// </summary>
internal sealed class TenancyRepository : ITenancyRepository
{
    private readonly RetailFlowDbContext _context;
    private readonly ITenantContext _tenant;

    public TenancyRepository(RetailFlowDbContext context, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);

        _context = context;
        _tenant = tenant;
    }

    public Task<Organization?> FindCurrentOrganizationAsync(CancellationToken cancellationToken = default) =>
        _context.Organizations.FirstOrDefaultAsync(o => o.Id == _tenant.OrganizationId, cancellationToken);

    public Task<Organization?> FindAnyOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        _context.Organizations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

    public async Task<bool> OrganizationCodeExistsAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = code.Trim().ToUpperInvariant();

        return await _context.Organizations
            .IgnoreQueryFilters()
            .AnyAsync(o => o.Code == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddOrganization(Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        _context.Organizations.Add(organization);
    }

    public Task<OrganizationSettings?> FindCurrentOrganizationSettingsAsync(
        CancellationToken cancellationToken = default) =>
        _context.OrganizationSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task<SettingsScope> GetSettingsScopeAsync(
        Guid organizationId,
        Guid? branchId,
        Guid? terminalId,
        CancellationToken cancellationToken = default)
    {
        var organizationSettings = await _context.OrganizationSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false);

        BranchSettings? branchSettings = null;
        Guid? defaultWarehouseId = null;

        if (branchId is { } branch)
        {
            branchSettings = await _context.BranchSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    s => s.OrganizationId == organizationId && s.BranchId == branch,
                    cancellationToken)
                .ConfigureAwait(false);

            defaultWarehouseId = await _context.Warehouses
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.OrganizationId == organizationId
                    && w.BranchId == branch
                    && w.IsDefault
                    && w.IsActive)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        PosTerminal? terminal = null;

        if (terminalId is { } till)
        {
            terminal = await _context.PosTerminals
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.OrganizationId == organizationId && t.Id == till,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new SettingsScope(organizationSettings, branchSettings, terminal, defaultWarehouseId);
    }

    public void AddOrganizationSettings(OrganizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _context.OrganizationSettings.Add(settings);
    }

    public Task<Branch?> FindBranchAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

    public async Task<IReadOnlyList<Branch>> GetBranchesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Branches.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(b => b.IsActive);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> BranchCodeExistsAsync(
        string code,
        Guid? exceptBranchId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        return await _context.Branches
            .AnyAsync(
                b => b.Code == normalized && (exceptBranchId == null || b.Id != exceptBranchId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> CountBranchesAsync(CancellationToken cancellationToken = default) =>
        _context.Branches.CountAsync(cancellationToken);

    public void AddBranch(Branch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        _context.Branches.Add(branch);
    }

    public Task<BranchSettings?> FindBranchSettingsAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        _context.BranchSettings.FirstOrDefaultAsync(s => s.BranchId == branchId, cancellationToken);

    public void AddBranchSettings(BranchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _context.BranchSettings.Add(settings);
    }

    public Task<Warehouse?> FindWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken = default) =>
        _context.Warehouses.FirstOrDefaultAsync(w => w.Id == warehouseId, cancellationToken);

    public async Task<IReadOnlyList<Warehouse>> GetWarehousesAsync(
        Guid? branchId,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Warehouses.AsQueryable();

        if (branchId is { } branch)
        {
            query = query.Where(w => w.BranchId == branch);
        }

        if (!includeInactive)
        {
            query = query.Where(w => w.IsActive);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WarehouseCodeExistsAsync(
        string code,
        Guid? exceptWarehouseId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        return await _context.Warehouses
            .AnyAsync(
                w => w.Code == normalized && (exceptWarehouseId == null || w.Id != exceptWarehouseId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> CountWarehousesAsync(CancellationToken cancellationToken = default) =>
        _context.Warehouses.CountAsync(cancellationToken);

    public void AddWarehouse(Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        _context.Warehouses.Add(warehouse);
    }

    public Task<PosTerminal?> FindTerminalAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
        _context.PosTerminals.FirstOrDefaultAsync(t => t.Id == terminalId, cancellationToken);

    public async Task<IReadOnlyList<PosTerminal>> GetTerminalsAsync(
        Guid? branchId,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PosTerminals.AsQueryable();

        if (branchId is { } branch)
        {
            query = query.Where(t => t.BranchId == branch);
        }

        if (!includeInactive)
        {
            query = query.Where(t => t.IsActive);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TerminalCodeExistsAsync(
        string code,
        Guid? exceptTerminalId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        return await _context.PosTerminals
            .AnyAsync(
                t => t.Code == normalized && (exceptTerminalId == null || t.Id != exceptTerminalId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> CountTerminalsAsync(CancellationToken cancellationToken = default) =>
        _context.PosTerminals.CountAsync(cancellationToken);

    public void AddTerminal(PosTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        _context.PosTerminals.Add(terminal);
    }

    public async Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(
        bool includeWithdrawn,
        CancellationToken cancellationToken = default)
    {
        var query = _context.SubscriptionPlans.AsNoTracking();

        if (!includeWithdrawn)
        {
            query = query.Where(p => p.IsActive);
        }

        return await query
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.MonthlyPrice)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<SubscriptionPlan?> FindPlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
        _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

    public async Task<SubscriptionPlan?> FindPlanByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();

        return await _context.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Code == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<OrganizationSubscription?> FindSubscriptionAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        _context.OrganizationSubscriptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);

    public void AddSubscription(OrganizationSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        _context.OrganizationSubscriptions.Add(subscription);
    }
}

using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>Lists the tenant's branches (FR-TEN-01).</summary>
public sealed class ListBranchesHandler : IUseCase<ListBranchesRequest, IReadOnlyList<BranchResponse>>
{
    private readonly ITenancyRepository _repository;

    public ListBranchesHandler(ITenancyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<BranchResponse>>> HandleAsync(
        ListBranchesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branches = await _repository
            .GetBranchesAsync(request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        // One read for every branch's default warehouse rather than a query per branch: the list
        // screen shows it, and a per-row lookup is how a five-branch list becomes eleven queries.
        var warehouses = await _repository
            .GetWarehousesAsync(branchId: null, includeInactive: false, cancellationToken)
            .ConfigureAwait(false);

        var defaults = warehouses
            .Where(w => w.IsDefault)
            .GroupBy(w => w.BranchId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        return branches
            .OrderBy(b => b.Code, StringComparer.Ordinal)
            .Select(b => TenancyMapping.ToResponse(
                b,
                defaults.TryGetValue(b.Id, out var warehouseId) ? warehouseId : null))
            .ToArray();
    }
}

/// <summary>Returns one branch.</summary>
public sealed class GetBranchHandler : IUseCase<GetBranchRequest, BranchResponse>
{
    private readonly ITenancyRepository _repository;

    public GetBranchHandler(ITenancyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<BranchResponse>> HandleAsync(
        GetBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branch = await _repository.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return TenancyErrors.BranchNotFound();
        }

        var warehouses = await _repository
            .GetWarehousesAsync(branch.Id, includeInactive: false, cancellationToken)
            .ConfigureAwait(false);

        return TenancyMapping.ToResponse(branch, warehouses.FirstOrDefault(w => w.IsDefault)?.Id);
    }
}

/// <summary>
/// Creates a branch, with its settings row and — unless the caller says otherwise — a default
/// warehouse, because a branch that cannot hold stock is not yet usable (FR-TEN-01).
/// </summary>
public sealed class CreateBranchHandler : IUseCase<CreateBranchRequest, BranchResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IPlanLimitGuard _limits;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;
    private readonly IClock _clock;
    private readonly TenancyOptions _options;

    public CreateBranchHandler(
        ITenancyRepository repository,
        IPlanLimitGuard limits,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids,
        IClock clock,
        TenancyOptions options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository;
        _limits = limits;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<BranchResponse>> HandleAsync(
        CreateBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Inside the handler's transaction, so the count it checks is the count it adds to.
        var capacity = await _limits
            .EnsureCapacityAsync(PlanFeatureCode.Branches, cancellationToken)
            .ConfigureAwait(false);

        if (capacity.IsFailure)
        {
            return capacity.Error;
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.BranchCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return TenancyErrors.BranchCodeTaken(code);
        }

        var organization = await _repository
            .FindCurrentOrganizationAsync(cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            return TenancyErrors.OrganizationNotFound();
        }

        var now = _clock.UtcNow;

        var branch = Branch.Create(
            _guids.NewId(now),
            _tenant.OrganizationId,
            code,
            request.Name,
            string.IsNullOrWhiteSpace(request.TimeZoneId) ? organization.TimeZoneId : request.TimeZoneId,
            request.BusinessDayStart);

        branch.UpdateDetails(
            request.Name,
            request.NameBn,
            request.AddressLine,
            request.City,
            request.PhoneNumber);

        _repository.AddBranch(branch);
        _repository.AddBranchSettings(
            BranchSettings.CreateDefault(_guids.NewId(now), _tenant.OrganizationId, branch.Id));

        Guid? defaultWarehouseId = null;

        if (request.CreateDefaultWarehouse)
        {
            var warehouseCapacity = await _limits
                .EnsureCapacityAsync(PlanFeatureCode.Warehouses, cancellationToken)
                .ConfigureAwait(false);

            if (warehouseCapacity.IsFailure)
            {
                return warehouseCapacity.Error;
            }

            // Derived from the branch code so it is unique per tenant without asking the caller
            // for a second code they do not care about.
            var warehouseCode = Truncate($"{code}-{_options.DefaultWarehouseCode}", 32);

            if (await _repository
                .WarehouseCodeExistsAsync(warehouseCode, null, cancellationToken)
                .ConfigureAwait(false))
            {
                return TenancyErrors.WarehouseCodeTaken(warehouseCode);
            }

            var warehouse = Warehouse.Create(
                _guids.NewId(now),
                _tenant.OrganizationId,
                branch.Id,
                warehouseCode,
                request.Name + " Store",
                WarehouseType.Main,
                isDefault: true);

            _repository.AddWarehouse(warehouse);
            defaultWarehouseId = warehouse.Id;
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.branch_created",
                nameof(Branch),
                branch.Id,
                After: new { branch.Code, branch.Name, branch.TimeZoneId, DefaultWarehouseId = defaultWarehouseId }),
            cancellationToken).ConfigureAwait(false);

        return TenancyMapping.ToResponse(branch, defaultWarehouseId);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}

/// <summary>Changes a branch's details and trading calendar.</summary>
public sealed class UpdateBranchHandler : IUseCase<UpdateBranchRequest, BranchResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;

    public UpdateBranchHandler(
        ITenancyRepository repository,
        IEffectiveSettingsProvider settings,
        ITenantContext tenant,
        IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _settings = settings;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<Result<BranchResponse>> HandleAsync(
        UpdateBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branch = await _repository.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return TenancyErrors.BranchNotFound();
        }

        var before = TenancyMapping.ToResponse(branch, null);

        branch.UpdateDetails(
            request.Name,
            request.NameBn,
            request.AddressLine,
            request.City,
            request.PhoneNumber);

        if (!string.IsNullOrWhiteSpace(request.TimeZoneId) || request.BusinessDayStart is not null)
        {
            branch.SetBusinessCalendar(
                string.IsNullOrWhiteSpace(request.TimeZoneId) ? branch.TimeZoneId : request.TimeZoneId,
                request.BusinessDayStart ?? branch.BusinessDayStart);
        }

        var warehouses = await _repository
            .GetWarehousesAsync(branch.Id, includeInactive: false, cancellationToken)
            .ConfigureAwait(false);

        var response = TenancyMapping.ToResponse(branch, warehouses.FirstOrDefault(w => w.IsDefault)?.Id);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.branch_updated",
                nameof(Branch),
                branch.Id,
                Before: before,
                After: response),
            cancellationToken).ConfigureAwait(false);

        // The branch's time zone feeds the business date on every transaction, so a cached
        // resolution for this tenant must not outlive the change.
        await _settings.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);

        return response;
    }
}

/// <summary>
/// Activates or deactivates a branch. Deactivation keeps every record: it stops new trading at that
/// location, it does not erase what happened there (NFR-09).
/// </summary>
public sealed class SetBranchActivationHandler : IUseCase<SetBranchActivationRequest, BranchResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IAuditWriter _audit;

    public SetBranchActivationHandler(ITenancyRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<BranchResponse>> HandleAsync(
        SetBranchActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branch = await _repository.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return TenancyErrors.BranchNotFound();
        }

        if (!request.IsActive)
        {
            var branches = await _repository
                .GetBranchesAsync(includeInactive: false, cancellationToken)
                .ConfigureAwait(false);

            // A tenant with no active branch cannot sell anything, and the state is easy to reach
            // by accident on a single-branch shop.
            if (branches.Count(b => b.IsActive) <= 1 && branch.IsActive)
            {
                return Error.Conflict(
                    "tenancy.last_active_branch",
                    "This is the only active branch. Create another before deactivating it.");
            }
        }

        if (request.IsActive)
        {
            branch.Activate();
        }
        else
        {
            branch.Deactivate();
        }

        await _audit.WriteAsync(
            new AuditEntry(
                request.IsActive ? "tenancy.branch_activated" : "tenancy.branch_deactivated",
                nameof(Branch),
                branch.Id,
                After: new { branch.Code, branch.IsActive }),
            cancellationToken).ConfigureAwait(false);

        return TenancyMapping.ToResponse(branch, null);
    }
}

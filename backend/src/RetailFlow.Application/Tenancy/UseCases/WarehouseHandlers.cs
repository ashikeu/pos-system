using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>Lists warehouses, optionally for one branch.</summary>
public sealed class ListWarehousesHandler : IUseCase<ListWarehousesRequest, IReadOnlyList<WarehouseResponse>>
{
    private readonly ITenancyRepository _repository;

    public ListWarehousesHandler(ITenancyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<WarehouseResponse>>> HandleAsync(
        ListWarehousesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warehouses = await _repository
            .GetWarehousesAsync(request.BranchId, request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        return warehouses
            .OrderBy(w => w.Code, StringComparer.Ordinal)
            .Select(TenancyMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>
/// Creates a warehouse in a branch. Making it the default moves the flag off the previous one in
/// the same transaction, so "the branch default" never means two rows (FR-TEN-01).
/// </summary>
public sealed class CreateWarehouseHandler : IUseCase<CreateWarehouseRequest, WarehouseResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IPlanLimitGuard _limits;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;
    private readonly IClock _clock;

    public CreateWarehouseHandler(
        ITenancyRepository repository,
        IPlanLimitGuard limits,
        IEffectiveSettingsProvider settings,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _limits = limits;
        _settings = settings;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
        _clock = clock;
    }

    public async Task<Result<WarehouseResponse>> HandleAsync(
        CreateWarehouseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branch = await _repository.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return TenancyErrors.BranchNotFound();
        }

        var existing = await _repository
            .GetWarehousesAsync(request.BranchId, includeInactive: true, cancellationToken)
            .ConfigureAwait(false);

        // A plan without multi-warehouse support still gets the one warehouse a branch needs to
        // hold stock; what it does not get is a second one.
        if (existing.Count > 0)
        {
            var multiWarehouse = await _limits
                .EnsureEnabledAsync(PlanFeatureCode.MultiWarehouse, cancellationToken)
                .ConfigureAwait(false);

            if (multiWarehouse.IsFailure)
            {
                return multiWarehouse.Error;
            }
        }

        var capacity = await _limits
            .EnsureCapacityAsync(PlanFeatureCode.Warehouses, cancellationToken)
            .ConfigureAwait(false);

        if (capacity.IsFailure)
        {
            return capacity.Error;
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.WarehouseCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return TenancyErrors.WarehouseCodeTaken(code);
        }

        Warehouse warehouse;

        try
        {
            warehouse = Warehouse.Create(
                _guids.NewId(_clock.UtcNow),
                _tenant.OrganizationId,
                request.BranchId,
                code,
                request.Name,
                request.Type,
                request.MakeDefault);
        }
        catch (DomainException ex)
        {
            return Error.Validation("tenancy.warehouse_invalid", ex.Message);
        }

        if (request.MakeDefault)
        {
            foreach (var other in existing.Where(w => w.IsDefault))
            {
                other.ClearDefault();
            }
        }

        _repository.AddWarehouse(warehouse);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.warehouse_created",
                nameof(Warehouse),
                warehouse.Id,
                After: new { warehouse.Code, warehouse.Name, warehouse.Type, warehouse.IsDefault },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["branchId"] = request.BranchId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        if (request.MakeDefault)
        {
            await _settings.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
        }

        return TenancyMapping.ToResponse(warehouse);
    }
}

/// <summary>Renames a warehouse and optionally makes it the branch default.</summary>
public sealed class UpdateWarehouseHandler : IUseCase<UpdateWarehouseRequest, WarehouseResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;

    public UpdateWarehouseHandler(
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

    public async Task<Result<WarehouseResponse>> HandleAsync(
        UpdateWarehouseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warehouse = await _repository
            .FindWarehouseAsync(request.WarehouseId, cancellationToken)
            .ConfigureAwait(false);

        if (warehouse is null)
        {
            return TenancyErrors.WarehouseNotFound();
        }

        var before = TenancyMapping.ToResponse(warehouse);
        warehouse.Rename(request.Name);

        if (request.MakeDefault && !warehouse.IsDefault)
        {
            var siblings = await _repository
                .GetWarehousesAsync(warehouse.BranchId, includeInactive: true, cancellationToken)
                .ConfigureAwait(false);

            foreach (var other in siblings.Where(w => w.IsDefault && w.Id != warehouse.Id))
            {
                other.ClearDefault();
            }

            try
            {
                warehouse.MakeDefault();
            }
            catch (DomainException ex)
            {
                return Error.Validation("tenancy.warehouse_invalid", ex.Message);
            }

            await _settings.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);
        }

        var response = TenancyMapping.ToResponse(warehouse);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.warehouse_updated",
                nameof(Warehouse),
                warehouse.Id,
                Before: before,
                After: response),
            cancellationToken).ConfigureAwait(false);

        return response;
    }
}

/// <summary>Activates or deactivates a warehouse.</summary>
public sealed class SetWarehouseActivationHandler : IUseCase<SetWarehouseActivationRequest, WarehouseResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IAuditWriter _audit;

    public SetWarehouseActivationHandler(ITenancyRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<WarehouseResponse>> HandleAsync(
        SetWarehouseActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warehouse = await _repository
            .FindWarehouseAsync(request.WarehouseId, cancellationToken)
            .ConfigureAwait(false);

        if (warehouse is null)
        {
            return TenancyErrors.WarehouseNotFound();
        }

        try
        {
            if (request.IsActive)
            {
                warehouse.Activate();
            }
            else
            {
                // Refused while it is the branch default: stock movements resolve to the default,
                // so deactivating it would break selling at that branch rather than tidy it up.
                warehouse.Deactivate();
            }
        }
        catch (DomainException ex)
        {
            return Error.Conflict("tenancy.warehouse_in_use", ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                request.IsActive ? "tenancy.warehouse_activated" : "tenancy.warehouse_deactivated",
                nameof(Warehouse),
                warehouse.Id,
                After: new { warehouse.Code, warehouse.IsActive }),
            cancellationToken).ConfigureAwait(false);

        return TenancyMapping.ToResponse(warehouse);
    }
}

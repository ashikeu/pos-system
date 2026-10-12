using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>Returns the caller's own organization (FR-TEN-02).</summary>
public sealed class GetOrganizationHandler : IUseCase<GetOrganizationRequest, OrganizationResponse>
{
    private readonly ITenancyRepository _repository;

    public GetOrganizationHandler(ITenancyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<OrganizationResponse>> HandleAsync(
        GetOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var organization = await _repository
            .FindCurrentOrganizationAsync(cancellationToken)
            .ConfigureAwait(false);

        return organization is null
            ? TenancyErrors.OrganizationNotFound()
            : TenancyMapping.ToResponse(organization);
    }
}

/// <summary>Changes the organization's own details (FR-TEN-07: audited).</summary>
public sealed class UpdateOrganizationHandler : IUseCase<UpdateOrganizationRequest, OrganizationResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IAuditWriter _audit;

    public UpdateOrganizationHandler(ITenancyRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<OrganizationResponse>> HandleAsync(
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var organization = await _repository
            .FindCurrentOrganizationAsync(cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            return TenancyErrors.OrganizationNotFound();
        }

        var before = TenancyMapping.ToResponse(organization);

        organization.UpdateProfile(
            request.Name,
            request.NameBn,
            request.AddressLine,
            request.City,
            request.PhoneNumber,
            request.ContactEmail);
        organization.SetLegalIdentity(request.LegalName, request.BinNumber);

        if (!string.IsNullOrWhiteSpace(request.TimeZoneId) || !string.IsNullOrWhiteSpace(request.DefaultLanguage))
        {
            organization.SetLocalization(
                string.IsNullOrWhiteSpace(request.TimeZoneId) ? organization.TimeZoneId : request.TimeZoneId,
                string.IsNullOrWhiteSpace(request.DefaultLanguage)
                    ? organization.DefaultLanguage
                    : request.DefaultLanguage);
        }

        var after = TenancyMapping.ToResponse(organization);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.organization_updated",
                nameof(Organization),
                organization.Id,
                Before: before,
                After: after),
            cancellationToken).ConfigureAwait(false);

        return after;
    }
}

/// <summary>
/// Blocks a tenant's writes (FR-TEN-06). Sessions are deliberately left alive: the shop should be
/// able to sign in, read its data and settle the account, which is the whole point of a read-only
/// state rather than a lock-out (ADR-0030).
/// </summary>
public sealed class SuspendOrganizationHandler : IUseCase<SuspendOrganizationRequest, bool>
{
    private readonly ITenancyRepository _repository;
    private readonly ITenantStatusProvider _status;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public SuspendOrganizationHandler(
        ITenancyRepository repository,
        ITenantStatusProvider status,
        IAuditWriter audit,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _status = status;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<bool>> HandleAsync(
        SuspendOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("tenancy.reason_required", "Give a reason for the suspension.");
        }

        var organization = await _repository
            .FindAnyOrganizationAsync(request.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            return TenancyErrors.OrganizationNotFound();
        }

        if (organization.Status == OrganizationStatus.Closed)
        {
            return TenancyErrors.TenantClosed();
        }

        organization.Suspend(_clock.UtcNow, request.Reason);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.organization_suspended",
                nameof(Organization),
                organization.Id,
                After: new { organization.Status, organization.SuspendedReason },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["organizationCode"] = organization.Code,
                }),
            cancellationToken).ConfigureAwait(false);

        await _status.InvalidateAsync(organization.Id, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>Lets a suspended tenant trade again.</summary>
public sealed class ReactivateOrganizationHandler : IUseCase<ReactivateOrganizationRequest, bool>
{
    private readonly ITenancyRepository _repository;
    private readonly ITenantStatusProvider _status;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public ReactivateOrganizationHandler(
        ITenancyRepository repository,
        ITenantStatusProvider status,
        IAuditWriter audit,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _status = status;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<bool>> HandleAsync(
        ReactivateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var organization = await _repository
            .FindAnyOrganizationAsync(request.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            return TenancyErrors.OrganizationNotFound();
        }

        if (organization.Status == OrganizationStatus.Closed)
        {
            return TenancyErrors.TenantClosed();
        }

        organization.Reactivate(_clock.UtcNow);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.organization_reactivated",
                nameof(Organization),
                organization.Id,
                After: new { organization.Status },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["organizationCode"] = organization.Code,
                }),
            cancellationToken).ConfigureAwait(false);

        await _status.InvalidateAsync(organization.Id, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>
/// Returns settings as they apply to a scope, together with the overrides each level stores
/// (FR-TEN-04). The client uses the merged values and shows the overrides on the settings screen,
/// so "inherited" and "set here" stay distinguishable.
/// </summary>
public sealed class GetEffectiveSettingsHandler : IUseCase<GetEffectiveSettingsRequest, EffectiveSettingsResponse>
{
    private readonly IEffectiveSettingsProvider _settings;
    private readonly ITenancyRepository _repository;
    private readonly ITenantContext _tenant;

    public GetEffectiveSettingsHandler(
        IEffectiveSettingsProvider settings,
        ITenancyRepository repository,
        ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenant);

        _settings = settings;
        _repository = repository;
        _tenant = tenant;
    }

    public async Task<Result<EffectiveSettingsResponse>> HandleAsync(
        GetEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branchId = request.BranchId ?? _tenant.BranchId;
        var terminalId = request.TerminalId ?? _tenant.TerminalId;

        if (request.BranchId is { } requested)
        {
            // A branch id in the query string is still checked against this tenant's branches; the
            // caller's branch *grant* was already checked by the middleware for the scope header.
            var branch = await _repository.FindBranchAsync(requested, cancellationToken).ConfigureAwait(false);
            if (branch is null)
            {
                return TenancyErrors.BranchNotFound();
            }
        }

        var effective = await _settings
            .GetAsync(_tenant.OrganizationId, branchId, terminalId, cancellationToken)
            .ConfigureAwait(false);

        var scope = await _repository
            .GetSettingsScopeAsync(_tenant.OrganizationId, branchId, terminalId, cancellationToken)
            .ConfigureAwait(false);

        return new EffectiveSettingsResponse(
            effective,
            scope.Organization?.ToValues() ?? new OrganizationSettingsValues(),
            scope.Branch?.ToValues());
    }
}

/// <summary>Replaces the organization's settings overrides.</summary>
public sealed class UpdateOrganizationSettingsHandler
    : IUseCase<UpdateOrganizationSettingsRequest, EffectiveSettingsResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public UpdateOrganizationSettingsHandler(
        ITenancyRepository repository,
        IEffectiveSettingsProvider settings,
        IUnitOfWork unitOfWork,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _settings = settings;
        _unitOfWork = unitOfWork;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<EffectiveSettingsResponse>> HandleAsync(
        UpdateOrganizationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _repository
            .FindCurrentOrganizationSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings is null)
        {
            // A tenant provisioned before this table existed, or one created directly in a test:
            // create the row rather than refusing to save a legitimate change.
            settings = OrganizationSettings.CreateDefault(_guids.NewId(), _tenant.OrganizationId);
            _repository.AddOrganizationSettings(settings);
        }

        var before = settings.ToValues();
        settings.Apply(request.Values);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.organization_settings_updated",
                nameof(OrganizationSettings),
                settings.Id,
                Before: before,
                After: settings.ToValues()),
            cancellationToken).ConfigureAwait(false);

        // Flushed inside the pipeline's transaction before the answer is re-resolved: the provider
        // reads the database, so without this it would hand back the values this call replaced.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _settings.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);

        var effective = await _settings
            .GetAsync(_tenant.OrganizationId, _tenant.BranchId, _tenant.TerminalId, cancellationToken)
            .ConfigureAwait(false);

        return new EffectiveSettingsResponse(effective, settings.ToValues(), null);
    }
}

/// <summary>Replaces a branch's settings overrides.</summary>
public sealed class UpdateBranchSettingsHandler
    : IUseCase<UpdateBranchSettingsRequest, EffectiveSettingsResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public UpdateBranchSettingsHandler(
        ITenancyRepository repository,
        IEffectiveSettingsProvider settings,
        IUnitOfWork unitOfWork,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _settings = settings;
        _unitOfWork = unitOfWork;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<EffectiveSettingsResponse>> HandleAsync(
        UpdateBranchSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var branch = await _repository.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return TenancyErrors.BranchNotFound();
        }

        var settings = await _repository
            .FindBranchSettingsAsync(request.BranchId, cancellationToken)
            .ConfigureAwait(false);

        if (settings is null)
        {
            settings = BranchSettings.CreateDefault(_guids.NewId(), _tenant.OrganizationId, request.BranchId);
            _repository.AddBranchSettings(settings);
        }

        var before = settings.ToValues();
        settings.Apply(request.Values);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.branch_settings_updated",
                nameof(BranchSettings),
                settings.Id,
                Before: before,
                After: settings.ToValues(),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["branchId"] = request.BranchId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        // See the organization handler: the provider reads the database, so the change has to be
        // flushed before it is re-resolved.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _settings.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);

        var effective = await _settings
            .GetAsync(_tenant.OrganizationId, request.BranchId, terminalId: null, cancellationToken)
            .ConfigureAwait(false);

        var organizationSettings = await _repository
            .FindCurrentOrganizationSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        return new EffectiveSettingsResponse(
            effective,
            organizationSettings?.ToValues() ?? new OrganizationSettingsValues(),
            settings.ToValues());
    }
}

/// <summary>Mapping between tenancy entities and their contracts. Entities are never returned.</summary>
internal static class TenancyMapping
{
    public static OrganizationResponse ToResponse(Organization organization) =>
        new(
            organization.Id,
            organization.Code,
            organization.Name,
            organization.NameBn,
            organization.LegalName,
            organization.BinNumber,
            organization.AddressLine,
            organization.City,
            organization.PhoneNumber,
            organization.ContactEmail,
            organization.CurrencyCode,
            organization.TimeZoneId,
            organization.DefaultLanguage,
            organization.Status,
            organization.SuspendedAtUtc,
            organization.SuspendedReason);

    public static BranchResponse ToResponse(Branch branch, Guid? defaultWarehouseId) =>
        new(
            branch.Id,
            branch.Code,
            branch.Name,
            branch.NameBn,
            branch.AddressLine,
            branch.City,
            branch.PhoneNumber,
            branch.TimeZoneId,
            branch.BusinessDayStart,
            branch.IsActive,
            defaultWarehouseId);

    public static WarehouseResponse ToResponse(Warehouse warehouse) =>
        new(
            warehouse.Id,
            warehouse.BranchId,
            warehouse.Code,
            warehouse.Name,
            warehouse.Type,
            warehouse.IsDefault,
            warehouse.IsActive);

    public static TerminalResponse ToResponse(PosTerminal terminal) =>
        new(
            terminal.Id,
            terminal.BranchId,
            terminal.Code,
            terminal.Name,
            terminal.DefaultWarehouseId,
            terminal.ReceiptTemplate58,
            terminal.ReceiptTemplate80,
            terminal.IsActive,
            terminal.LastSyncAtUtc,
            terminal.AppVersion);

    public static PlanResponse ToResponse(SubscriptionPlan plan) =>
        new(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.Description,
            plan.MonthlyPrice,
            plan.CurrencyCode,
            plan.TrialDays,
            plan.IsActive,
            plan.Features
                .OrderBy(f => f.Code)
                .Select(f => new PlanFeatureResponse(f.Code, f.MaxCount, f.IsEnabled))
                .ToArray());
}

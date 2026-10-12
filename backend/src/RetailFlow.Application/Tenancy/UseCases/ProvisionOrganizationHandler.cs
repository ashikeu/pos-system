using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.UseCases;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>Error codes the tenancy module returns; part of the API contract (ADR-0014).</summary>
public static class TenancyErrors
{
    public static Error OrganizationCodeTaken(string code) => Error.Conflict(
        "tenancy.organization_code_taken",
        $"The code '{code}' is already in use. Choose another.");

    public static Error OrganizationNotFound() => Error.NotFound(
        "tenancy.organization_not_found",
        "That organization does not exist.");

    public static Error BranchNotFound() => Error.NotFound(
        "tenancy.branch_not_found",
        "That branch does not exist.");

    public static Error BranchCodeTaken(string code) => Error.Conflict(
        "tenancy.branch_code_taken",
        $"A branch with the code '{code}' already exists.");

    public static Error WarehouseNotFound() => Error.NotFound(
        "tenancy.warehouse_not_found",
        "That warehouse does not exist.");

    public static Error WarehouseCodeTaken(string code) => Error.Conflict(
        "tenancy.warehouse_code_taken",
        $"A warehouse with the code '{code}' already exists.");

    public static Error TerminalNotFound() => Error.NotFound(
        "tenancy.terminal_not_found",
        "That terminal does not exist.");

    public static Error TerminalCodeTaken(string code) => Error.Conflict(
        "tenancy.terminal_code_taken",
        $"A terminal with the code '{code}' already exists.");

    public static Error PlanNotFound(string code) => Error.NotFound(
        "tenancy.plan_not_found",
        $"There is no subscription plan with the code '{code}'.");

    public static Error SubscriptionNotFound() => Error.NotFound(
        "tenancy.subscription_not_found",
        "This organization has no subscription.");

    /// <summary>Returned when a plan limit is reached (FR-TEN-05).</summary>
    public static Error PlanLimitReached(PlanFeatureCode code, int max) => Error.Conflict(
        "tenancy.plan_limit_reached",
        $"Your plan allows at most {max} {Describe(code)}. Upgrade the plan to add more.");

    public static Error FeatureNotInPlan(PlanFeatureCode code) => Error.Conflict(
        "tenancy.feature_not_in_plan",
        $"Your plan does not include {Describe(code)}.");

    /// <summary>Returned to a suspended or lapsed tenant that attempts a write (FR-TEN-06).</summary>
    public static Error TenantReadOnly(string reason) => Error.Forbidden(
        "tenancy.organization_read_only",
        reason);

    public static Error TenantClosed() => Error.Forbidden(
        "tenancy.organization_closed",
        "This organization is closed. Contact support.");

    public static Error BranchHasNoWarehouse() => Error.Conflict(
        "tenancy.branch_has_no_warehouse",
        "That branch has no warehouse to sell from. Create one first.");

    public static Error WarehouseBranchMismatch() => Error.Validation(
        "tenancy.warehouse_branch_mismatch",
        "That warehouse belongs to a different branch.");

    private static string Describe(PlanFeatureCode code) => code switch
    {
        PlanFeatureCode.Branches => "branches",
        PlanFeatureCode.Warehouses => "warehouses",
        PlanFeatureCode.PosTerminals => "POS terminals",
        PlanFeatureCode.Users => "users",
        PlanFeatureCode.Products => "products",
        PlanFeatureCode.OfflineMode => "offline selling",
        PlanFeatureCode.Loyalty => "the loyalty module",
        PlanFeatureCode.CreditSales => "credit sales",
        PlanFeatureCode.MultiWarehouse => "more than one warehouse per branch",
        _ => code.ToString(),
    };
}

/// <summary>
/// Creates a tenant that can trade immediately (FR-TEN-01, FR-TEN-05): organization, settings,
/// first branch with its default warehouse, one terminal, the role templates, an owner account and
/// a subscription — all in one transaction, so a half-provisioned tenant cannot exist.
/// </summary>
/// <remarks>
/// <para>
/// This is the one handler that writes rows for an organization other than the ambient one, because
/// it is the operation that brings that organization into existence. It therefore switches the
/// ambient tenant for the duration of its writes and restores it in a <c>finally</c>: the save
/// interceptor makes no exception for provisioning, and the idempotency decorator finishes its own
/// bookkeeping under the caller's tenant after this handler returns (ADR-0004, ADR-0025).
/// </para>
/// <para>
/// Authorization is the platform-operator permission on the endpoint. There is deliberately no
/// anonymous self-service signup: an open endpoint that creates tenants and accounts is a spam and
/// resource-exhaustion surface, and nothing in the product needs it yet (ADR-0030).
/// </para>
/// </remarks>
public sealed class ProvisionOrganizationHandler
    : IUseCase<ProvisionOrganizationRequest, ProvisionOrganizationResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IIdentityProvisioning _identity;
    private readonly IMutableTenantContext _mutableTenant;
    private readonly ITenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly TenancyOptions _options;
    private readonly AuthenticationOptions _authentication;

    public ProvisionOrganizationHandler(
        ITenancyRepository repository,
        IIdentityProvisioning identity,
        IMutableTenantContext mutableTenant,
        ITenantContext tenant,
        IUnitOfWork unitOfWork,
        IAuditWriter audit,
        IClock clock,
        IGuidGenerator guids,
        TenancyOptions options,
        AuthenticationOptions authentication)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(mutableTenant);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authentication);

        _repository = repository;
        _identity = identity;
        _mutableTenant = mutableTenant;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _clock = clock;
        _guids = guids;
        _options = options;
        _authentication = authentication;
    }

    public async Task<Result<ProvisionOrganizationResponse>> HandleAsync(
        ProvisionOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var passwordCheck = PasswordRules.Validate(request.OwnerPassword, _authentication.MinimumPasswordLength);
        if (passwordCheck.IsFailure)
        {
            return passwordCheck.Error;
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.OrganizationCodeExistsAsync(code, cancellationToken).ConfigureAwait(false))
        {
            return TenancyErrors.OrganizationCodeTaken(code);
        }

        var planCode = string.IsNullOrWhiteSpace(request.PlanCode)
            ? _options.DefaultPlanCode
            : request.PlanCode.Trim().ToUpperInvariant();

        var plan = await _repository.FindPlanByCodeAsync(planCode, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return TenancyErrors.PlanNotFound(planCode);
        }

        var now = _clock.UtcNow;

        var organization = Organization.Create(
            _guids.NewId(now),
            code,
            request.Name,
            request.CurrencyCode,
            request.TimeZoneId,
            request.DefaultLanguage);
        organization.SetLegalIdentity(request.LegalName, request.BinNumber);
        organization.UpdateProfile(
            request.Name,
            nameBn: null,
            request.AddressLine,
            request.City,
            request.PhoneNumber,
            request.OwnerEmail);

        Guid branchId;
        Guid warehouseId;
        Guid terminalId;
        Guid subscriptionId;
        DateOnly? trialEndsOn;
        Guid ownerUserId;

        // Every row below belongs to the new tenant, so the ambient tenant has to be that tenant
        // while they are written — and flushed — exactly as it would be for any later request.
        using (TenantScopeSwitch.To(_mutableTenant, _tenant, organization.Id))
        {
            _repository.AddOrganization(organization);
            _repository.AddOrganizationSettings(
                OrganizationSettings.CreateDefault(_guids.NewId(now), organization.Id));

            var branch = Branch.Create(
                _guids.NewId(now),
                organization.Id,
                _options.DefaultBranchCode,
                string.IsNullOrWhiteSpace(request.BranchName) ? "Main Branch" : request.BranchName.Trim(),
                organization.TimeZoneId);
            branch.UpdateDetails(
                branch.Name,
                nameBn: null,
                request.AddressLine,
                request.City,
                request.PhoneNumber);
            _repository.AddBranch(branch);
            _repository.AddBranchSettings(
                BranchSettings.CreateDefault(_guids.NewId(now), organization.Id, branch.Id));
            branchId = branch.Id;

            var warehouse = Warehouse.Create(
                _guids.NewId(now),
                organization.Id,
                branch.Id,
                _options.DefaultWarehouseCode,
                "Main Store",
                WarehouseType.Main,
                isDefault: true);
            _repository.AddWarehouse(warehouse);
            warehouseId = warehouse.Id;

            var terminal = PosTerminal.Create(
                _guids.NewId(now),
                organization.Id,
                branch.Id,
                _options.DefaultTerminalCode,
                "Counter 1");
            terminal.UpdateDetails(terminal.Name, warehouse.Id, receiptTemplate58: null, receiptTemplate80: null);
            _repository.AddTerminal(terminal);
            terminalId = terminal.Id;

            var subscription = OrganizationSubscription.Start(
                _guids.NewId(now),
                organization.Id,
                plan.Id,
                DateOnly.FromDateTime(now.UtcDateTime),
                _options.DefaultPeriodDays,
                plan.TrialDays,
                _options.GraceDays);
            _repository.AddSubscription(subscription);
            subscriptionId = subscription.Id;
            trialEndsOn = subscription.TrialEndsOn;

            // Saved before the identity work so the organization, branch and roles it references
            // are already rows: the owner's branch grant has a real branch to point at.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await _identity.EnsureSystemRolesAsync(organization.Id, cancellationToken).ConfigureAwait(false);

            var owner = await _identity
                .CreateOwnerAsync(
                    organization.Id,
                    branch.Id,
                    new OwnerAccountRequest(request.OwnerEmail, request.OwnerFullName, request.OwnerPassword),
                    cancellationToken)
                .ConfigureAwait(false);

            if (owner.IsFailure)
            {
                return owner.Error;
            }

            ownerUserId = owner.Value;
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.organization_provisioned",
                nameof(Organization),
                organization.Id,
                After: new
                {
                    organization.Code,
                    organization.Name,
                    PlanCode = plan.Code,
                    BranchId = branchId,
                    TerminalId = terminalId,
                    OwnerUserId = ownerUserId,
                },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["organizationId"] = organization.Id.ToString(),
                    ["idempotencyKey"] = request.IdempotencyKey,
                }),
            cancellationToken).ConfigureAwait(false);

        return new ProvisionOrganizationResponse(
            organization.Id,
            organization.Code,
            branchId,
            warehouseId,
            terminalId,
            ownerUserId,
            subscriptionId,
            plan.Code,
            trialEndsOn);
    }
}

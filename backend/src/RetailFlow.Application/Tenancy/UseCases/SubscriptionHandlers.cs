using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>Lists the plans on offer, with their limits (FR-TEN-05).</summary>
public sealed class ListPlansHandler : IUseCase<ListPlansRequest, IReadOnlyList<PlanResponse>>
{
    private readonly ITenancyRepository _repository;

    public ListPlansHandler(ITenancyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<PlanResponse>>> HandleAsync(
        ListPlansRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plans = await _repository
            .GetPlansAsync(request.IncludeWithdrawn, cancellationToken)
            .ConfigureAwait(false);

        return plans.Select(TenancyMapping.ToResponse).ToArray();
    }
}

/// <summary>Returns the tenant's subscription and how much of each limit it is using.</summary>
public sealed class GetSubscriptionHandler : IUseCase<GetSubscriptionRequest, SubscriptionResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IPlanLimitGuard _limits;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    public GetSubscriptionHandler(
        ITenancyRepository repository,
        IPlanLimitGuard limits,
        ITenantContext tenant,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _limits = limits;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<Result<SubscriptionResponse>> HandleAsync(
        GetSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var subscription = await _repository
            .FindSubscriptionAsync(_tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (subscription is null)
        {
            return TenancyErrors.SubscriptionNotFound();
        }

        var plan = await _repository.FindPlanAsync(subscription.PlanId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return TenancyErrors.PlanNotFound(subscription.PlanId.ToString());
        }

        var usage = await _limits.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        return new SubscriptionResponse(
            subscription.Id,
            plan.Id,
            plan.Code,
            plan.Name,
            subscription.Status,
            subscription.StartsOn,
            subscription.CurrentPeriodEndsOn,
            subscription.TrialEndsOn,
            subscription.WriteableUntil,
            subscription.AllowsWrites(today),
            usage);
    }
}

/// <summary>
/// Moves the tenant to another plan. A downgrade that the tenant's current usage would already
/// breach is refused with the specific limit named: silently accepting it would leave a shop whose
/// every attempt to add a till fails, with no obvious cause (FR-TEN-05).
/// </summary>
public sealed class ChangePlanHandler : IUseCase<ChangePlanRequest, SubscriptionResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IPlanLimitGuard _limits;
    private readonly ITenantStatusProvider _status;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public ChangePlanHandler(
        ITenancyRepository repository,
        IPlanLimitGuard limits,
        ITenantStatusProvider status,
        ITenantContext tenant,
        IAuditWriter audit,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _limits = limits;
        _status = status;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<SubscriptionResponse>> HandleAsync(
        ChangePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var planCode = (request.PlanCode ?? string.Empty).Trim().ToUpperInvariant();

        var plan = await _repository.FindPlanByCodeAsync(planCode, cancellationToken).ConfigureAwait(false);
        if (plan is null || !plan.IsActive)
        {
            return TenancyErrors.PlanNotFound(planCode);
        }

        var subscription = await _repository
            .FindSubscriptionAsync(_tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (subscription is null)
        {
            return TenancyErrors.SubscriptionNotFound();
        }

        var counts = await _limits.GetCountsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var feature in plan.Features)
        {
            if (!counts.TryGetValue(feature.Code, out var used))
            {
                continue;
            }

            if (!feature.IsEnabled && used > 0)
            {
                return TenancyErrors.FeatureNotInPlan(feature.Code);
            }

            if (feature.MaxCount is { } max && used > max)
            {
                return Error.Conflict(
                    "tenancy.plan_below_current_usage",
                    $"The {plan.Name} plan allows {max}, and this organization already has {used}. "
                        + "Remove some first, or choose a larger plan.");
            }
        }

        var previousPlanId = subscription.PlanId;

        try
        {
            subscription.ChangePlan(plan.Id);
        }
        catch (DomainException ex)
        {
            return Error.Conflict("tenancy.plan_change_refused", ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.plan_changed",
                nameof(OrganizationSubscription),
                subscription.Id,
                Before: new { PlanId = previousPlanId },
                After: new { PlanId = plan.Id, plan.Code }),
            cancellationToken).ConfigureAwait(false);

        // Feature switches change what the tenant may do, and the status snapshot carries them.
        await _status.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);

        var usage = await _limits.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        return new SubscriptionResponse(
            subscription.Id,
            plan.Id,
            plan.Code,
            plan.Name,
            subscription.Status,
            subscription.StartsOn,
            subscription.CurrentPeriodEndsOn,
            subscription.TrialEndsOn,
            subscription.WriteableUntil,
            subscription.AllowsWrites(today),
            usage);
    }
}

/// <summary>
/// Extends a tenant's paid period (platform operators only). This is the renewal half of the
/// read-only surface a lapsed tenant gets: writes come back as soon as the period does.
/// </summary>
public sealed class RenewSubscriptionHandler : IUseCase<RenewSubscriptionRequest, bool>
{
    private readonly ITenancyRepository _repository;
    private readonly ITenantStatusProvider _status;
    private readonly IMutableTenantContext _mutableTenant;
    private readonly ITenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _audit;

    public RenewSubscriptionHandler(
        ITenancyRepository repository,
        ITenantStatusProvider status,
        IMutableTenantContext mutableTenant,
        ITenantContext tenant,
        IUnitOfWork unitOfWork,
        IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(mutableTenant);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _status = status;
        _mutableTenant = mutableTenant;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _audit = audit;
    }

    public async Task<Result<bool>> HandleAsync(
        RenewSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = await _repository
            .FindSubscriptionAsync(request.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (subscription is null)
        {
            return TenancyErrors.SubscriptionNotFound();
        }

        var before = new { subscription.Status, subscription.CurrentPeriodEndsOn };

        // The subscription belongs to the tenant being renewed, not to the operator running this,
        // so the change is made and flushed as that tenant (ADR-0004).
        using (TenantScopeSwitch.To(_mutableTenant, _tenant, request.OrganizationId))
        {
            try
            {
                subscription.Renew(request.PeriodEndsOn);
            }
            catch (DomainException ex)
            {
                return Error.Validation("tenancy.renewal_invalid", ex.Message);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.subscription_renewed",
                nameof(OrganizationSubscription),
                subscription.Id,
                Before: before,
                After: new { subscription.Status, subscription.CurrentPeriodEndsOn },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["organizationId"] = request.OrganizationId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        await _status.InvalidateAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

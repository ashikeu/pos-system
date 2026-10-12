using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.UseCases;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Infrastructure.Tenancy.Services;

/// <summary>
/// Enforces the limits on a tenant's plan (FR-TEN-05). Counts are read live, never cached: the
/// check runs inside the transaction that is about to add the thing being counted, so a cached
/// count would be exactly the wrong number.
/// </summary>
/// <remarks>
/// Three deliberate leniencies, so the guard never blocks a tenant on missing information: no
/// subscription row means no limits, a plan that says nothing about a feature does not limit it,
/// and a feature whose counter does not exist yet (<see cref="PlanFeatureCode.Products"/> until the
/// catalog lands in Phase 5) is reported but not enforced.
/// </remarks>
internal sealed class PlanLimitGuard : IPlanLimitGuard
{
    private readonly ITenancyRepository _repository;
    private readonly IIdentityProvisioning _identity;
    private readonly ITenantContext _tenant;

    public PlanLimitGuard(
        ITenancyRepository repository,
        IIdentityProvisioning identity,
        ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        _repository = repository;
        _identity = identity;
        _tenant = tenant;
    }

    public async Task<Result> EnsureCapacityAsync(
        PlanFeatureCode code,
        CancellationToken cancellationToken = default)
    {
        var feature = await FindFeatureAsync(code, cancellationToken).ConfigureAwait(false);

        if (feature is null)
        {
            return Result.Success();
        }

        if (!feature.IsEnabled)
        {
            return Result.Failure(TenancyErrors.FeatureNotInPlan(code));
        }

        if (feature.MaxCount is not { } max)
        {
            return Result.Success();
        }

        var used = await CountAsync(code, cancellationToken).ConfigureAwait(false);

        if (used is null)
        {
            return Result.Success();
        }

        return used >= max
            ? Result.Failure(TenancyErrors.PlanLimitReached(code, max))
            : Result.Success();
    }

    public async Task<Result> EnsureEnabledAsync(
        PlanFeatureCode code,
        CancellationToken cancellationToken = default)
    {
        var feature = await FindFeatureAsync(code, cancellationToken).ConfigureAwait(false);

        return feature is null || feature.IsEnabled
            ? Result.Success()
            : Result.Failure(TenancyErrors.FeatureNotInPlan(code));
    }

    public async Task<IReadOnlyList<PlanLimitUsage>> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var plan = await FindPlanAsync(cancellationToken).ConfigureAwait(false);

        if (plan is null)
        {
            return [];
        }

        var usage = new List<PlanLimitUsage>(plan.Features.Count);

        foreach (var feature in plan.Features.OrderBy(f => f.Code))
        {
            var used = await CountAsync(feature.Code, cancellationToken).ConfigureAwait(false);

            usage.Add(new PlanLimitUsage(
                feature.Code,
                used ?? 0,
                feature.MaxCount,
                feature.IsEnabled,
                IsCounted: used is not null));
        }

        return usage;
    }

    public async Task<IReadOnlyDictionary<PlanFeatureCode, int>> GetCountsAsync(
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<PlanFeatureCode, int>();

        foreach (var code in Countable)
        {
            var used = await CountAsync(code, cancellationToken).ConfigureAwait(false);

            if (used is { } value)
            {
                counts[code] = value;
            }
        }

        return counts;
    }

    private static readonly PlanFeatureCode[] Countable =
    [
        PlanFeatureCode.Branches,
        PlanFeatureCode.Warehouses,
        PlanFeatureCode.PosTerminals,
        PlanFeatureCode.Users,
    ];

    private async Task<PlanFeature?> FindFeatureAsync(PlanFeatureCode code, CancellationToken cancellationToken)
    {
        var plan = await FindPlanAsync(cancellationToken).ConfigureAwait(false);
        return plan?.Features.FirstOrDefault(f => f.Code == code);
    }

    private async Task<SubscriptionPlan?> FindPlanAsync(CancellationToken cancellationToken)
    {
        var subscription = await _repository
            .FindSubscriptionAsync(_tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        return subscription is null
            ? null
            : await _repository.FindPlanAsync(subscription.PlanId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How many of the thing the tenant has, or <c>null</c> when this build cannot count it yet.
    /// Null is reported as "not counted" rather than as zero, so an unimplemented counter can never
    /// be mistaken for an empty tenant and used to enforce a limit.
    /// </summary>
    private async Task<int?> CountAsync(PlanFeatureCode code, CancellationToken cancellationToken) => code switch
    {
        PlanFeatureCode.Branches =>
            await _repository.CountBranchesAsync(cancellationToken).ConfigureAwait(false),
        PlanFeatureCode.Warehouses =>
            await _repository.CountWarehousesAsync(cancellationToken).ConfigureAwait(false),
        PlanFeatureCode.PosTerminals =>
            await _repository.CountTerminalsAsync(cancellationToken).ConfigureAwait(false),
        PlanFeatureCode.Users =>
            await _identity.CountActiveUsersAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false),

        // Products arrive with the catalog in Phase 5; the switch features below are not counted
        // at all, they are simply on or off.
        _ => null,
    };
}

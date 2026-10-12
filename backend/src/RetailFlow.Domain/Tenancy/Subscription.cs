using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>
/// What a plan limits (FR-TEN-05). The codes are part of the application's contract, like
/// permissions are (ADR-0013): a plan row may be edited by whoever sells the software, but the set
/// of things that can be limited changes only when the software does.
/// </summary>
public enum PlanFeatureCode
{
    /// <summary>How many branches the tenant may have.</summary>
    Branches = 0,

    /// <summary>How many warehouses, across all branches.</summary>
    Warehouses = 1,

    /// <summary>How many POS terminals, across all branches.</summary>
    PosTerminals = 2,

    /// <summary>How many user accounts, counting only active ones.</summary>
    Users = 3,

    /// <summary>How many products. Counted from Phase 5, when the catalog exists.</summary>
    Products = 4,

    /// <summary>Whether terminals may sell while offline (ADR-0017).</summary>
    OfflineMode = 5,

    /// <summary>Whether the loyalty module is available.</summary>
    Loyalty = 6,

    /// <summary>Whether selling on customer credit is available.</summary>
    CreditSales = 7,

    /// <summary>Whether the tenant may use more than one warehouse per branch.</summary>
    MultiWarehouse = 8,
}

/// <summary>Where a tenant stands with its subscription.</summary>
public enum SubscriptionStatus
{
    /// <summary>Inside a free trial; full access until the trial ends.</summary>
    Trialing = 0,

    /// <summary>Paid and current.</summary>
    Active = 1,

    /// <summary>Payment is late but the grace period has not run out: still writable.</summary>
    PastDue = 2,

    /// <summary>Period ended without renewal: read-only (FR-TEN-06).</summary>
    Expired = 3,

    /// <summary>Cancelled by the customer or the operator: read-only once the period ends.</summary>
    Cancelled = 4,
}

/// <summary>
/// A sellable plan. Platform-level reference data, not tenant-owned: every organization sees the
/// same catalog of plans, and a plan's limits are what provisioning and the limit guard read.
/// </summary>
/// <remarks>
/// Plainly an <see cref="AggregateRoot"/> rather than an auditable one: the per-tenant change
/// sequence that <see cref="AuditableAggregateRoot"/> carries is meaningless for a row no tenant
/// owns, and offline clients do not sync the price list (ADR-0018).
/// </remarks>
public sealed class SubscriptionPlan : AggregateRoot
{
    private readonly List<PlanFeature> _features = [];

    private SubscriptionPlan()
    {
    }

    private SubscriptionPlan(
        Guid id,
        string code,
        string name,
        decimal monthlyPrice,
        string currencyCode,
        int trialDays,
        int sortOrder)
        : base(id)
    {
        Code = code;
        Name = name;
        MonthlyPrice = monthlyPrice;
        CurrencyCode = currencyCode;
        TrialDays = trialDays;
        SortOrder = sortOrder;
        IsActive = true;
    }

    /// <summary>Short stable code, unique across the platform, e.g. <c>STARTER</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Recurring price per month, in <see cref="CurrencyCode"/>.</summary>
    public decimal MonthlyPrice { get; private set; }

    public string CurrencyCode { get; private set; } = Money.DefaultCurrencyCode;

    /// <summary>Length of the free trial a new tenant on this plan gets; zero for none.</summary>
    public int TrialDays { get; private set; }

    /// <summary>Display order on a pricing page.</summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// Whether the plan may be chosen now. A withdrawn plan stays in the table because tenants are
    /// still on it; it simply stops being offered.
    /// </summary>
    public bool IsActive { get; private set; }

    public IReadOnlyCollection<PlanFeature> Features => _features;

    public static SubscriptionPlan Create(
        Guid id,
        string code,
        string name,
        decimal monthlyPrice,
        string? currencyCode = null,
        int trialDays = 0,
        int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Plan code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Plan name is required.", nameof(name));
        }

        if (monthlyPrice < 0m)
        {
            throw new DomainException("A plan price must not be negative.");
        }

        if (trialDays < 0)
        {
            throw new DomainException("Trial length must not be negative.");
        }

        return new SubscriptionPlan(
            id,
            code.Trim().ToUpperInvariant(),
            name.Trim(),
            RoundingPolicy.Round4(monthlyPrice),
            (currencyCode ?? Money.DefaultCurrencyCode).Trim().ToUpperInvariant(),
            trialDays,
            sortOrder);
    }

    public void Describe(string? description) =>
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>
    /// Sets a limit on this plan, replacing any existing one for the same feature. A null
    /// <paramref name="maxCount"/> means unlimited, which is different from absent: an absent
    /// feature is unlimited too, but a stated unlimited survives a later tightening of the
    /// defaults.
    /// </summary>
    public PlanFeature SetFeature(Guid featureId, PlanFeatureCode code, int? maxCount, bool isEnabled = true)
    {
        if (maxCount is { } max && max < 0)
        {
            throw new DomainException("A feature limit must not be negative.");
        }

        var existing = _features.FirstOrDefault(f => f.Code == code);
        if (existing is not null)
        {
            existing.Update(maxCount, isEnabled);
            return existing;
        }

        var feature = PlanFeature.Create(featureId, Id, code, maxCount, isEnabled);
        _features.Add(feature);
        return feature;
    }

    public void Withdraw() => IsActive = false;

    public void Reinstate() => IsActive = true;
}

/// <summary>One limit or switch belonging to a plan.</summary>
public sealed class PlanFeature : Entity
{
    private PlanFeature()
    {
    }

    private PlanFeature(Guid id, Guid planId, PlanFeatureCode code, int? maxCount, bool isEnabled)
        : base(id)
    {
        PlanId = planId;
        Code = code;
        MaxCount = maxCount;
        IsEnabled = isEnabled;
    }

    public Guid PlanId { get; private set; }

    public PlanFeatureCode Code { get; private set; }

    /// <summary>Maximum number allowed, or null for unlimited. Ignored for switch features.</summary>
    public int? MaxCount { get; private set; }

    /// <summary>Whether the feature is available at all on this plan.</summary>
    public bool IsEnabled { get; private set; }

    internal static PlanFeature Create(Guid id, Guid planId, PlanFeatureCode code, int? maxCount, bool isEnabled) =>
        new(id, planId, code, maxCount, isEnabled);

    internal void Update(int? maxCount, bool isEnabled)
    {
        MaxCount = maxCount;
        IsEnabled = isEnabled;
    }
}

/// <summary>
/// A tenant's subscription to a plan. One row per subscription period chain: changing plan updates
/// this record rather than creating a parallel one, because "which plan is this shop on" must have
/// exactly one answer at any instant.
/// </summary>
public sealed class OrganizationSubscription : TenantEntity
{
    private OrganizationSubscription()
    {
    }

    private OrganizationSubscription(
        Guid id,
        Guid organizationId,
        Guid planId,
        SubscriptionStatus status,
        DateOnly startsOn,
        DateOnly currentPeriodEndsOn,
        DateOnly? trialEndsOn)
        : base(id, organizationId)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("Plan id must not be empty.", nameof(planId));
        }

        if (currentPeriodEndsOn < startsOn)
        {
            throw new DomainException("A subscription period cannot end before it starts.");
        }

        PlanId = planId;
        Status = status;
        StartsOn = startsOn;
        CurrentPeriodEndsOn = currentPeriodEndsOn;
        TrialEndsOn = trialEndsOn;
    }

    public Guid PlanId { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    /// <summary>First day of the subscription, in branch-local business dates (ADR-0007).</summary>
    public DateOnly StartsOn { get; private set; }

    /// <summary>Last day the current paid period covers, inclusive.</summary>
    public DateOnly CurrentPeriodEndsOn { get; private set; }

    public DateOnly? TrialEndsOn { get; private set; }

    /// <summary>
    /// Days after <see cref="CurrentPeriodEndsOn"/> during which the tenant keeps writing while a
    /// payment is chased. A shop that cannot ring up sales stops trading, so a late invoice is not
    /// an instant switch-off.
    /// </summary>
    public int GraceDays { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public string? CancellationReason { get; private set; }

    public static OrganizationSubscription Start(
        Guid id,
        Guid organizationId,
        Guid planId,
        DateOnly startsOn,
        int periodDays,
        int trialDays = 0,
        int graceDays = 7)
    {
        if (periodDays <= 0)
        {
            throw new DomainException("A subscription period must be at least one day.");
        }

        if (graceDays < 0)
        {
            throw new DomainException("Grace days must not be negative.");
        }

        var trialEndsOn = trialDays > 0 ? startsOn.AddDays(trialDays) : (DateOnly?)null;

        var subscription = new OrganizationSubscription(
            id,
            organizationId,
            planId,
            trialDays > 0 ? SubscriptionStatus.Trialing : SubscriptionStatus.Active,
            startsOn,

            // A trial's period end is the trial end, so the same expiry check covers both cases.
            trialEndsOn ?? startsOn.AddDays(periodDays),
            trialEndsOn)
        {
            GraceDays = graceDays,
        };

        return subscription;
    }

    /// <summary>Moves the tenant to another plan, keeping the current period.</summary>
    public void ChangePlan(Guid planId)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("Plan id must not be empty.", nameof(planId));
        }

        if (Status is SubscriptionStatus.Cancelled)
        {
            throw new DomainException("A cancelled subscription cannot change plan; start a new one.");
        }

        PlanId = planId;
    }

    /// <summary>Extends the paid period and clears a late-payment state.</summary>
    public void Renew(DateOnly newPeriodEndsOn)
    {
        if (newPeriodEndsOn <= CurrentPeriodEndsOn)
        {
            throw new DomainException("A renewal must extend the current period.");
        }

        CurrentPeriodEndsOn = newPeriodEndsOn;
        Status = SubscriptionStatus.Active;
        TrialEndsOn = null;
        CancelledAtUtc = null;
        CancellationReason = null;
    }

    /// <summary>Marks the subscription late. Writes continue until the grace period runs out.</summary>
    public void MarkPastDue() => Status = SubscriptionStatus.PastDue;

    public void MarkExpired() => Status = SubscriptionStatus.Expired;

    public void Cancel(DateTimeOffset atUtc, string? reason)
    {
        Status = SubscriptionStatus.Cancelled;
        CancelledAtUtc = atUtc;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    /// <summary>The last day on which writes are still accepted, grace included.</summary>
    public DateOnly WriteableUntil => CurrentPeriodEndsOn.AddDays(GraceDays);

    /// <summary>
    /// Whether the subscription still permits writing on <paramref name="today"/>. Expired and
    /// cancelled subscriptions are read-only immediately; everything else gets its grace period
    /// (FR-TEN-06).
    /// </summary>
    public bool AllowsWrites(DateOnly today) => Status switch
    {
        SubscriptionStatus.Expired => false,
        SubscriptionStatus.Cancelled => today <= CurrentPeriodEndsOn,
        _ => today <= WriteableUntil,
    };
}

using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.Abstractions;

/// <summary>
/// Tenancy settings, bound from the <c>Tenancy</c> configuration section (ADR-0023). The defaults
/// here are the bottom of the configuration chain, so changing one moves every tenant that never
/// overrode it (FR-TEN-04).
/// </summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    public PlatformSettingsDefaults Defaults { get; init; } = new();

    /// <summary>Plan a newly provisioned organization starts on, by code.</summary>
    public string DefaultPlanCode { get; init; } = "STARTER";

    /// <summary>Length of a billing period in days; 30 unless a deployment bills differently.</summary>
    public int DefaultPeriodDays { get; init; } = 30;

    /// <summary>
    /// Days after a period ends during which a tenant may still write. A shop that cannot ring up
    /// sales stops trading, so a late payment is chased rather than enforced instantly (FR-TEN-06).
    /// </summary>
    public int GraceDays { get; init; } = 7;

    /// <summary>Code and name of the default branch provisioning creates.</summary>
    public string DefaultBranchCode { get; init; } = "MAIN";

    public string DefaultWarehouseCode { get; init; } = "MAIN";

    public string DefaultTerminalCode { get; init; } = "POS1";

    /// <summary>
    /// How long a tenant-status answer may be reused before it is read again. Short, because it
    /// decides whether writes are accepted: a suspended tenant must stop within seconds, and the
    /// alternative — checking on every request — puts a query on the path of every call.
    /// </summary>
    public int StatusCacheSeconds { get; init; } = 30;

    /// <summary>How long resolved settings may be cached. Invalidated on every settings change.</summary>
    public int SettingsCacheSeconds { get; init; } = 300;
}

/// <summary>How many of a limited thing a tenant has, against what its plan allows.</summary>
/// <param name="Code">The limited feature.</param>
/// <param name="Used">How many exist now.</param>
/// <param name="Max">The plan's maximum, or null for unlimited.</param>
/// <param name="IsEnabled">Whether the plan includes the feature at all.</param>
/// <param name="IsCounted">
/// False for a feature whose counter is not implemented yet — <see cref="PlanFeatureCode.Products"/>
/// until the catalog exists (Phase 5). Such a limit is reported, never silently enforced as zero.
/// </param>
public sealed record PlanLimitUsage(
    PlanFeatureCode Code,
    int Used,
    int? Max,
    bool IsEnabled,
    bool IsCounted);

/// <summary>
/// Enforces subscription limits (FR-TEN-05). Called by the use case that is about to create the
/// limited thing, inside its transaction, so the count it reads is the count it is adding to.
/// </summary>
public interface IPlanLimitGuard
{
    /// <summary>
    /// Fails when the current tenant is at its plan's maximum for <paramref name="code"/>, or when
    /// the plan does not include the feature.
    /// </summary>
    Task<Result> EnsureCapacityAsync(PlanFeatureCode code, CancellationToken cancellationToken = default);

    /// <summary>Fails when the plan does not include the feature, without counting anything.</summary>
    Task<Result> EnsureEnabledAsync(PlanFeatureCode code, CancellationToken cancellationToken = default);

    /// <summary>Current usage against the plan, for the subscription screen.</summary>
    Task<IReadOnlyList<PlanLimitUsage>> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How many of each countable thing the tenant has, independent of any plan. Used to check
    /// that a plan change would not leave the tenant already over its new limits.
    /// </summary>
    Task<IReadOnlyDictionary<PlanFeatureCode, int>> GetCountsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolved configuration for a scope (FR-TEN-04). Cached per organization and branch, and
/// invalidated by the handlers that change settings — a till that keeps selling with last hour's
/// discount cap is a real cost, so the cache is short and explicitly cleared.
/// </summary>
public interface IEffectiveSettingsProvider
{
    /// <summary>Settings for the ambient tenant, branch and terminal.</summary>
    Task<EffectiveSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Settings for an explicit scope, for background work and sync snapshots.</summary>
    Task<EffectiveSettings> GetAsync(
        Guid organizationId,
        Guid? branchId,
        Guid? terminalId,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the cached answers for a tenant after a settings change.</summary>
    Task InvalidateAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether a tenant may read and write right now (FR-TEN-06). Suspension and an expired
/// subscription are both read-only states: the shop can still see its data and pay its bill.
/// </summary>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="Status">Lifecycle state of the organization.</param>
/// <param name="SubscriptionStatus">Subscription state, or null when the tenant has no plan row.</param>
/// <param name="PeriodEndsOn">Last day of the current subscription period, when there is one.</param>
/// <param name="AllowsWrites">Whether write operations are accepted.</param>
/// <param name="Reason">Why writes are blocked, for the message shown to the user.</param>
public sealed record TenantStatusSnapshot(
    Guid OrganizationId,
    OrganizationStatus Status,
    SubscriptionStatus? SubscriptionStatus,
    DateOnly? PeriodEndsOn,
    bool AllowsWrites,
    string? Reason)
{
    /// <summary>True when the tenant is closed: no surface at all, not even read-only.</summary>
    public bool IsClosed => Status == OrganizationStatus.Closed;
}

/// <summary>
/// Reads tenant status with a short cache. Separate from the settings provider because this answer
/// gates writes and must therefore go stale in seconds, not minutes.
/// </summary>
public interface ITenantStatusProvider
{
    Task<TenantStatusSnapshot?> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task InvalidateAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>The rows the settings merge reads, fetched together.</summary>
/// <param name="Organization">The tenant's settings row, or null when it has none yet.</param>
/// <param name="Branch">The branch's settings row, when a branch was named.</param>
/// <param name="Terminal">The terminal, when one was named and it belongs to that tenant.</param>
/// <param name="BranchDefaultWarehouseId">The branch's default warehouse, when it has one.</param>
public sealed record SettingsScope(
    OrganizationSettings? Organization,
    BranchSettings? Branch,
    PosTerminal? Terminal,
    Guid? BranchDefaultWarehouseId);

/// <summary>
/// Data access for the tenancy module. The organization-level reads here are the only ones in the
/// system that may run before a tenant context exists (provisioning creates the tenant), which is
/// why they live in one implementation (ADR-0004).
/// </summary>
public interface ITenancyRepository
{
    // --- organization ---

    /// <summary>The ambient tenant's own organization row, through the tenant filter.</summary>
    Task<Organization?> FindCurrentOrganizationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Any organization by id, bypassing the tenant filter. Only for platform-operator handlers,
    /// which carry <c>platform.tenants.manage</c> and are audited; a tenant-scoped handler must use
    /// <see cref="FindCurrentOrganizationAsync"/> instead (ADR-0004).
    /// </summary>
    Task<Organization?> FindAnyOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Cross-tenant by necessity: organization codes are unique platform-wide.</summary>
    Task<bool> OrganizationCodeExistsAsync(string code, CancellationToken cancellationToken = default);

    void AddOrganization(Organization organization);

    /// <summary>The ambient tenant's settings row, for the screen that edits it.</summary>
    Task<OrganizationSettings?> FindCurrentOrganizationSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything the four-level merge needs, in one call and for an explicit scope. The ids are
    /// always server-derived — the tenant from the token, the branch from a validated grant — so
    /// this read is scoped by argument rather than by the query filter, which lets background work
    /// and sync snapshots resolve settings for a tenant they are not "inside" (FR-TEN-04).
    /// </summary>
    Task<SettingsScope> GetSettingsScopeAsync(
        Guid organizationId,
        Guid? branchId,
        Guid? terminalId,
        CancellationToken cancellationToken = default);

    void AddOrganizationSettings(OrganizationSettings settings);

    // --- branches ---

    Task<Branch?> FindBranchAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Branch>> GetBranchesAsync(bool includeInactive, CancellationToken cancellationToken = default);

    Task<bool> BranchCodeExistsAsync(
        string code,
        Guid? exceptBranchId = null,
        CancellationToken cancellationToken = default);

    Task<int> CountBranchesAsync(CancellationToken cancellationToken = default);

    void AddBranch(Branch branch);

    Task<BranchSettings?> FindBranchSettingsAsync(Guid branchId, CancellationToken cancellationToken = default);

    void AddBranchSettings(BranchSettings settings);

    // --- warehouses ---

    Task<Warehouse?> FindWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Warehouse>> GetWarehousesAsync(
        Guid? branchId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> WarehouseCodeExistsAsync(
        string code,
        Guid? exceptWarehouseId = null,
        CancellationToken cancellationToken = default);

    Task<int> CountWarehousesAsync(CancellationToken cancellationToken = default);

    void AddWarehouse(Warehouse warehouse);

    // --- terminals ---

    Task<PosTerminal?> FindTerminalAsync(Guid terminalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PosTerminal>> GetTerminalsAsync(
        Guid? branchId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> TerminalCodeExistsAsync(
        string code,
        Guid? exceptTerminalId = null,
        CancellationToken cancellationToken = default);

    Task<int> CountTerminalsAsync(CancellationToken cancellationToken = default);

    void AddTerminal(PosTerminal terminal);

    // --- plans and subscriptions ---

    Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(
        bool includeWithdrawn,
        CancellationToken cancellationToken = default);

    Task<SubscriptionPlan?> FindPlanAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<SubscriptionPlan?> FindPlanByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<OrganizationSubscription?> FindSubscriptionAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    void AddSubscription(OrganizationSubscription subscription);
}

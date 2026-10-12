using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Tenancy.Services;

/// <summary>
/// Cache generation for a tenant's settings. Invalidation bumps this one key instead of trying to
/// enumerate every branch and terminal combination that may be cached; the stale entries are then
/// unreachable and expire on their own.
/// </summary>
internal sealed class SettingsGeneration
{
    public long Value { get; init; } = 1;
}

/// <summary>
/// Resolves the configuration chain and caches the answer per scope (FR-TEN-04). The cache is
/// short-lived and dropped explicitly by every handler that changes a setting, because a till
/// selling with last hour's discount cap or tax mode is a real financial error, not a stale page.
/// </summary>
internal sealed class EffectiveSettingsProvider : IEffectiveSettingsProvider
{
    private readonly ITenancyRepository _repository;
    private readonly ICacheService _cache;
    private readonly ITenantContext _tenant;
    private readonly TenancyOptions _options;

    public EffectiveSettingsProvider(
        ITenancyRepository repository,
        ICacheService cache,
        ITenantContext tenant,
        TenancyOptions options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository;
        _cache = cache;
        _tenant = tenant;
        _options = options;
    }

    public Task<EffectiveSettings> GetAsync(CancellationToken cancellationToken = default) =>
        GetAsync(_tenant.OrganizationId, _tenant.BranchId, _tenant.TerminalId, cancellationToken);

    public async Task<EffectiveSettings> GetAsync(
        Guid organizationId,
        Guid? branchId,
        Guid? terminalId,
        CancellationToken cancellationToken = default)
    {
        var generation = await GetGenerationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var key = $"tenancy:settings:{organizationId:N}:{generation}:{branchId:N}:{terminalId:N}";

        return await _cache.GetOrAddAsync(
            key,
            async ct =>
            {
                var scope = await _repository
                    .GetSettingsScopeAsync(organizationId, branchId, terminalId, ct)
                    .ConfigureAwait(false);

                return SettingsResolution.Resolve(
                    _options.Defaults,
                    scope.Organization,
                    scope.Branch,
                    scope.Terminal,
                    scope.BranchDefaultWarehouseId);
            },
            TimeSpan.FromSeconds(_options.SettingsCacheSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InvalidateAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var current = await GetGenerationAsync(organizationId, cancellationToken).ConfigureAwait(false);

        await _cache.SetAsync(
            GenerationKey(organizationId),
            new SettingsGeneration { Value = current + 1 },
            TimeSpan.FromDays(1),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> GetGenerationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var generation = await _cache.GetOrAddAsync(
            GenerationKey(organizationId),
            _ => Task.FromResult(new SettingsGeneration()),
            TimeSpan.FromDays(1),
            cancellationToken).ConfigureAwait(false);

        return generation.Value;
    }

    private static string GenerationKey(Guid organizationId) => $"tenancy:settings-generation:{organizationId:N}";
}

/// <summary>
/// Answers whether a tenant may write, from its lifecycle state and its subscription period
/// (FR-TEN-06). Cached for seconds only: this decides whether a sale is accepted, so it has to
/// follow a suspension almost immediately while still not costing a query per request.
/// </summary>
internal sealed class TenantStatusProvider : ITenantStatusProvider
{
    private readonly RetailFlowDbContext _context;
    private readonly ICacheService _cache;
    private readonly IClock _clock;
    private readonly TenancyOptions _options;

    public TenantStatusProvider(
        RetailFlowDbContext context,
        ICacheService cache,
        IClock clock,
        TenancyOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _context = context;
        _cache = cache;
        _clock = clock;
        _options = options;
    }

    public async Task<TenantStatusSnapshot?> GetAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            return null;
        }

        var cached = await _cache
            .GetAsync<TenantStatusSnapshot>(Key(organizationId), cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            return cached;
        }

        var snapshot = await LoadAsync(organizationId, cancellationToken).ConfigureAwait(false);

        if (snapshot is not null)
        {
            await _cache.SetAsync(
                Key(organizationId),
                snapshot,
                TimeSpan.FromSeconds(_options.StatusCacheSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    public Task InvalidateAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(Key(organizationId), cancellationToken);

    private async Task<TenantStatusSnapshot?> LoadAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        // Read outside the tenant filter and compare the key instead: this runs from middleware,
        // before the request's tenant scope is trusted for anything else.
        var organization = await _context.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.Status, o.SuspendedReason })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            return null;
        }

        var subscription = await _context.OrganizationSubscriptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false);

        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        return organization.Status switch
        {
            OrganizationStatus.Closed => new TenantStatusSnapshot(
                organizationId,
                organization.Status,
                subscription?.Status,
                subscription?.CurrentPeriodEndsOn,
                AllowsWrites: false,
                Reason: "This organization is closed."),

            OrganizationStatus.Suspended => new TenantStatusSnapshot(
                organizationId,
                organization.Status,
                subscription?.Status,
                subscription?.CurrentPeriodEndsOn,
                AllowsWrites: false,
                Reason: string.IsNullOrWhiteSpace(organization.SuspendedReason)
                    ? "This organization is suspended. You can still view data and renew."
                    : $"This organization is suspended: {organization.SuspendedReason}"),

            // A tenant with no subscription row is unrestricted. That is the case for a tenant
            // created directly in a test or before this table existed; provisioning always writes
            // one, so in production the branch below is the live path.
            _ when subscription is null => new TenantStatusSnapshot(
                organizationId,
                organization.Status,
                null,
                null,
                AllowsWrites: true,
                Reason: null),

            _ => new TenantStatusSnapshot(
                organizationId,
                organization.Status,
                subscription.Status,
                subscription.CurrentPeriodEndsOn,
                subscription.AllowsWrites(today),
                subscription.AllowsWrites(today)
                    ? null
                    : $"Your subscription ended on {subscription.CurrentPeriodEndsOn:yyyy-MM-dd}. "
                        + "Renew it to continue; your data stays available to view."),
        };
    }

    private static string Key(Guid organizationId) => $"tenancy:status:{organizationId:N}";
}

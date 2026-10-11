using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Identity.Services;

/// <summary>
/// Works out what a user may actually do (ADR-0013). Resolution order: the roles assigned to the
/// account, plus any role granted for the branch in scope, then per-branch overrides — where a
/// denial always wins over a grant, because an exception that takes a permission away is the one
/// that must not be overridden by a role somewhere else.
/// </summary>
internal sealed class PermissionResolver : IPermissionResolver
{
    /// <summary>
    /// Short cache keyed by user and permission version. A role change bumps the version, so the
    /// new set is used on the next request rather than when a TTL happens to expire.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly RetailFlowDbContext _context;
    private readonly ICacheService _cache;
    private readonly ILogger<PermissionResolver> _logger;

    public PermissionResolver(
        RetailFlowDbContext context,
        ICacheService cache,
        ILogger<PermissionResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PermissionSet> ResolveAsync(
        Guid userId,
        Guid? branchId,
        CancellationToken cancellationToken = default)
    {
        var version = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (int?)u.PermissionsVersion)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            // No such user in this tenant: an empty set, never a permissive default.
            return Empty(userId, branchId, 0);
        }

        var cacheKey = BuildKey(userId, version.Value, branchId);
        var cached = await _cache.GetAsync<CachedPermissionSet>(cacheKey, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            return cached.ToPermissionSet(userId, branchId);
        }

        var resolved = await LoadAsync(userId, version.Value, branchId, cancellationToken).ConfigureAwait(false);

        await _cache
            .SetAsync(cacheKey, CachedPermissionSet.From(resolved), CacheDuration, cancellationToken)
            .ConfigureAwait(false);

        return resolved;
    }

    public Task InvalidateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // The version in the key does the real work; this removes the common entries eagerly so a
        // revocation is visible immediately even within the same version.
        PermissionLog.PermissionsInvalidated(_logger, userId);
        return _cache.RemoveAsync(BuildKey(userId, 0, null), cancellationToken);
    }

    private async Task<PermissionSet> LoadAsync(
        Guid userId,
        int version,
        Guid? branchId,
        CancellationToken cancellationToken)
    {
        // Role ids: those assigned to the account, plus the branch-specific role when one applies.
        var accountRoleIds = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var branchRoleIds = branchId is { } branch
            ? await _context.UserBranches
                .AsNoTracking()
                .Where(ub => ub.UserId == userId && ub.BranchId == branch && ub.RoleId != null)
                .Select(ub => ub.RoleId!.Value)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)
            : [];

        var roleIds = accountRoleIds.Concat(branchRoleIds).Distinct().ToArray();

        var roles = await _context.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var codes = await _context.RolePermissions
            .AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var effective = codes.ToHashSet(StringComparer.Ordinal);

        if (branchId is { } scopedBranch)
        {
            var overrides = await _context.UserBranchPermissions
                .AsNoTracking()
                .Where(p => p.UserId == userId && p.BranchId == scopedBranch)
                .Select(p => new { p.PermissionCode, p.IsGranted })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var grant in overrides.Where(o => o.IsGranted))
            {
                effective.Add(grant.PermissionCode);
            }

            // Denials applied last so they cannot be undone by a grant processed afterwards.
            foreach (var denial in overrides.Where(o => !o.IsGranted))
            {
                effective.Remove(denial.PermissionCode);
            }
        }

        // A code that no longer exists in the catalog is dropped rather than trusted: it means a
        // permission was removed from the application and the grant is stale.
        effective.RemoveWhere(code => !Permissions.Exists(code));

        return new PermissionSet(
            userId,
            version,
            branchId,
            effective,
            roles.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    private static PermissionSet Empty(Guid userId, Guid? branchId, int version) =>
        new(userId, version, branchId, new HashSet<string>(StringComparer.Ordinal), []);

    private static string BuildKey(Guid userId, int version, Guid? branchId) =>
        $"permissions:{userId:N}:{version}:{branchId?.ToString("N") ?? "org"}";

    /// <summary>Cache shape: a plain class, so a distributed cache can serialize it.</summary>
    internal sealed class CachedPermissionSet
    {
        public int PermissionsVersion { get; set; }

        public List<string> Codes { get; set; } = [];

        public List<string> RoleNames { get; set; } = [];

        public static CachedPermissionSet From(PermissionSet set) => new()
        {
            PermissionsVersion = set.PermissionsVersion,
            Codes = [.. set.Codes],
            RoleNames = [.. set.RoleNames],
        };

        public PermissionSet ToPermissionSet(Guid userId, Guid? branchId) => new(
            userId,
            PermissionsVersion,
            branchId,
            Codes.ToHashSet(StringComparer.Ordinal),
            RoleNames);
    }
}

/// <summary>
/// Caches the per-request security facts about a user (ADR-0027). Without this, every authenticated
/// call would read the user row to check the security stamp; with a long cache, a revocation would
/// not take effect until the token expired. Thirty seconds is the compromise.
/// </summary>
internal sealed class UserSecurityStateProvider : IUserSecurityStateProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly RetailFlowDbContext _context;
    private readonly ICacheService _cache;
    private readonly IClock _clock;

    public UserSecurityStateProvider(RetailFlowDbContext context, ICacheService cache, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);

        _context = context;
        _cache = cache;
        _clock = clock;
    }

    public async Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(userId);
        var cached = await _cache.GetAsync<CachedState>(key, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            return cached.ToState(userId);
        }

        var now = _clock.UtcNow;

        // Filters are bypassed deliberately: this runs while authenticating a request, before any
        // tenant context exists, and the token's subject already names the user.
        var snapshot = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.OrganizationId,
                u.SecurityStamp,
                u.PermissionsVersion,
                u.IsActive,
                u.LockoutEndAtUtc,
                u.IsPlatformOperator,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return null;
        }

        var branchIds = await _context.UserBranches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(ub => ub.UserId == userId)
            .Select(ub => ub.BranchId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var state = new CachedState
        {
            OrganizationId = snapshot.OrganizationId,
            SecurityStamp = snapshot.SecurityStamp,
            PermissionsVersion = snapshot.PermissionsVersion,
            IsActive = snapshot.IsActive,
            IsLockedOut = snapshot.LockoutEndAtUtc is { } until && until > now,
            IsPlatformOperator = snapshot.IsPlatformOperator,
            BranchIds = branchIds,
        };

        await _cache.SetAsync(key, state, CacheDuration, cancellationToken).ConfigureAwait(false);

        return state.ToState(userId);
    }

    public Task InvalidateAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(BuildKey(userId), cancellationToken);

    private static string BuildKey(Guid userId) => $"user-security:{userId:N}";

    internal sealed class CachedState
    {
        public Guid OrganizationId { get; set; }

        public string SecurityStamp { get; set; } = string.Empty;

        public int PermissionsVersion { get; set; }

        public bool IsActive { get; set; }

        public bool IsLockedOut { get; set; }

        public bool IsPlatformOperator { get; set; }

        public List<Guid> BranchIds { get; set; } = [];

        public UserSecurityState ToState(Guid userId) => new(
            userId,
            OrganizationId,
            SecurityStamp,
            PermissionsVersion,
            IsActive,
            IsLockedOut,
            IsPlatformOperator,
            BranchIds.ToHashSet());
    }
}

/// <summary>Log messages for permission resolution.</summary>
internal static partial class PermissionLog
{
    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Debug,
        Message = "Cached permissions invalidated for user {UserId}")]
    public static partial void PermissionsInvalidated(ILogger logger, Guid userId);
}

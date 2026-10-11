using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Identity.Persistence;

/// <summary>
/// Identity data access (ADR-0002). The sign-in reads deliberately bypass the tenant filter,
/// because a sign-in happens before any tenant context exists — there is no other way to resolve an
/// organization code or a refresh token. Those three methods are the only intentional
/// <c>IgnoreQueryFilters</c> calls in the system outside platform-operator endpoints, and each one
/// narrows the query by an exact code, hash or id rather than returning a list (ADR-0004).
/// </summary>
internal sealed class IdentityRepository : IIdentityRepository
{
    private readonly RetailFlowDbContext _context;

    public IdentityRepository(RetailFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<OrganizationLookup?> FindOrganizationByCodeAsync(
        string organizationCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organizationCode))
        {
            return null;
        }

        var normalized = organizationCode.Trim().ToUpperInvariant();

        return await _context.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Code == normalized)
            .Select(o => new OrganizationLookup(o.Id, o.Code, o.Name, o.Status == OrganizationStatus.Active))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OrganizationLookup?> FindOrganizationByIdAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        await _context.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new OrganizationLookup(o.Id, o.Code, o.Name, o.Status == OrganizationStatus.Active))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<User?> FindUserByEmailAsync(
        Guid organizationId,
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return null;
        }

        // Tracked, because the sign-in path updates lockout counters and may upgrade the hash.
        return await _context.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                u => u.OrganizationId == organizationId && u.NormalizedEmail == normalizedEmail,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RefreshToken?> FindRefreshTokenByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        return await _context.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RefreshToken>> GetFamilyTokensAsync(
        Guid familyId,
        CancellationToken cancellationToken = default) =>
        await _context.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.FamilyId == familyId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await _context.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<BranchGrant>> GetBranchGrantsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await (from grant in _context.UserBranches
               join branch in _context.Branches
                   on grant.BranchId equals branch.Id
               where grant.UserId == userId && branch.IsActive
               orderby branch.Code
               select new BranchGrant(branch.Id, branch.Code, branch.Name, grant.IsDefault, grant.RoleId))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await (from userRole in _context.UserRoles
               join role in _context.Roles
                   on userRole.RoleId equals role.Id
               where userRole.UserId == userId
               orderby role.Name
               select role.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<UserSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await _context.UserSessions
            .Where(s => s.UserId == userId && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<LoginAttempt>> GetLoginHistoryAsync(
        Guid organizationId,
        Guid userId,
        int take,
        CancellationToken cancellationToken = default) =>
        await _context.LoginAttempts
            .AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId)
            .OrderByDescending(a => a.AtUtc)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<MfaSecret?> FindMfaSecretAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await _context.MfaSecrets
            .Where(s => s.UserId == userId && s.DisabledAtUtc == null)
            .OrderByDescending(s => s.IssuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<UserToken?> FindUsableTokenAsync(
        Guid userId,
        UserTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        return await _context.UserTokens
            .FirstOrDefaultAsync(
                t => t.UserId == userId
                    && t.Purpose == purpose
                    && t.TokenHash == tokenHash
                    && t.ConsumedAtUtc == null
                    && t.ExpiresAtUtc > nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeviceRegistration?> FindDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        await _context.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> CountRecentFailuresAsync(
        string normalizedEmail,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default) =>
        await _context.LoginAttempts
            .AsNoTracking()
            .CountAsync(
                a => a.EmailAttempted == normalizedEmail && !a.Succeeded && a.AtUtc >= sinceUtc,
                cancellationToken)
            .ConfigureAwait(false);

    public void AddRefreshToken(RefreshToken token) => _context.RefreshTokens.Add(token);

    public void AddSession(UserSession session) => _context.UserSessions.Add(session);

    public void AddLoginAttempt(LoginAttempt attempt) => _context.LoginAttempts.Add(attempt);

    public void AddMfaSecret(MfaSecret secret) => _context.MfaSecrets.Add(secret);

    public void AddUserToken(UserToken token) => _context.UserTokens.Add(token);

    public void AddDevice(DeviceRegistration device) => _context.DeviceRegistrations.Add(device);
}

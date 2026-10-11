using RetailFlow.Domain.Identity;

namespace RetailFlow.Application.Identity.Abstractions;

/// <summary>The tenant facts a sign-in needs before any tenant context exists.</summary>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="Code">Its code.</param>
/// <param name="Name">Its display name.</param>
/// <param name="IsActive">False when suspended or closed: sign-in is refused (FR-TEN-06).</param>
public sealed record OrganizationLookup(Guid OrganizationId, string Code, string Name, bool IsActive);

/// <summary>A branch a user may act in.</summary>
/// <param name="BranchId">Branch identifier.</param>
/// <param name="Code">Branch code.</param>
/// <param name="Name">Branch name.</param>
/// <param name="IsDefault">Whether it is the user's default.</param>
/// <param name="RoleId">Branch-specific role, when one is granted.</param>
public sealed record BranchGrant(Guid BranchId, string Code, string Name, bool IsDefault, Guid? RoleId);

/// <summary>
/// Data access for the identity module. One port rather than a repository per entity: these
/// aggregates are always used together, and several of the reads here are deliberately
/// cross-tenant — resolving an organization code and a refresh token happens before any tenant
/// context exists, which is why those queries are confined to this one implementation (ADR-0004).
/// </summary>
public interface IIdentityRepository
{
    // --- sign-in path (runs without tenant context) ---

    Task<OrganizationLookup?> FindOrganizationByCodeAsync(
        string organizationCode,
        CancellationToken cancellationToken = default);

    Task<OrganizationLookup?> FindOrganizationByIdAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<User?> FindUserByEmailAsync(
        Guid organizationId,
        string normalizedEmail,
        CancellationToken cancellationToken = default);

    Task<RefreshToken?> FindRefreshTokenByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefreshToken>> GetFamilyTokensAsync(
        Guid familyId,
        CancellationToken cancellationToken = default);

    // --- tenant-scoped reads ---

    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BranchGrant>> GetBranchGrantsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<UserSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoginAttempt>> GetLoginHistoryAsync(
        Guid organizationId,
        Guid userId,
        int take,
        CancellationToken cancellationToken = default);

    Task<MfaSecret?> FindMfaSecretAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<UserToken?> FindUsableTokenAsync(
        Guid userId,
        UserTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistration?> FindDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Counts recent failed attempts for an address, for per-account rate limiting.</summary>
    Task<int> CountRecentFailuresAsync(
        string normalizedEmail,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);

    // --- writes ---

    void AddRefreshToken(RefreshToken token);

    void AddSession(UserSession session);

    void AddLoginAttempt(LoginAttempt attempt);

    void AddMfaSecret(MfaSecret secret);

    void AddUserToken(UserToken token);

    void AddDevice(DeviceRegistration device);
}

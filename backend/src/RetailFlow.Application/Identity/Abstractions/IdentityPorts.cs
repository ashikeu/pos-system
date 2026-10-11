using RetailFlow.Domain.Identity;

namespace RetailFlow.Application.Identity.Abstractions;

/// <summary>Outcome of verifying a password against a stored hash.</summary>
public enum PasswordVerificationResult
{
    Failed = 0,
    Success = 1,

    /// <summary>
    /// Correct, but hashed with outdated parameters. The caller re-hashes it with current
    /// parameters while it has the plaintext, so stored hashes strengthen over time (ADR-0012).
    /// </summary>
    SuccessRehashNeeded = 2,
}

/// <summary>
/// Password hashing. Behind a port so the algorithm can be upgraded (PBKDF2 to Argon2id) without
/// touching a use case, and so tests can use a fast hasher.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string hash, string password);
}

/// <summary>
/// Generates and hashes opaque secrets: refresh tokens, device secrets, reset tokens. Separate from
/// password hashing because these are high-entropy values that need a fast, deterministic digest
/// rather than a slow, salted one.
/// </summary>
public interface ISecureTokenGenerator
{
    /// <summary>Returns a URL-safe random token of at least 256 bits.</summary>
    string CreateToken();

    /// <summary>Returns a short numeric code, for an SMS or a supervisor PIN.</summary>
    string CreateNumericCode(int digits = 6);

    /// <summary>SHA-256 of a token, hex encoded. Used for storage and lookup.</summary>
    string Hash(string token);
}

/// <summary>Encrypts values that must be readable later but never readable from a backup.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

/// <summary>Claims written into an access token (ADR-0027).</summary>
/// <param name="UserId">Subject.</param>
/// <param name="OrganizationId">Tenant the token acts for.</param>
/// <param name="SessionId">Session, so a specific sign-in can be revoked.</param>
/// <param name="SecurityStamp">
/// Snapshot of the user's security stamp; validation rejects the token if the stamp has changed,
/// which is what makes a password change or a deactivation take effect before expiry.
/// </param>
/// <param name="PermissionsVersion">Version of the permission set the client last saw.</param>
/// <param name="BranchId">Branch selected at sign-in, if any.</param>
/// <param name="TerminalId">Terminal, when the caller is a POS device.</param>
/// <param name="DeviceId">Registered device, when present.</param>
/// <param name="Roles">Role names, for display only — authorization uses permissions.</param>
public sealed record AccessTokenRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid SessionId,
    string SecurityStamp,
    int PermissionsVersion,
    Guid? BranchId = null,
    Guid? TerminalId = null,
    Guid? DeviceId = null,
    IReadOnlyCollection<string>? Roles = null);

/// <summary>An issued access token and when it expires.</summary>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc, int ExpiresInSeconds);

/// <summary>Issues signed access tokens.</summary>
public interface IAccessTokenService
{
    AccessToken Issue(AccessTokenRequest request);
}

/// <summary>Time-based one-time passwords for optional MFA (FR-SEC-06).</summary>
public interface ITotpService
{
    /// <summary>Creates a new base32 shared secret.</summary>
    string CreateSecret();

    /// <summary>
    /// Verifies a code, allowing for clock drift. Returns the accepted time step so the caller can
    /// reject a replay of the same code.
    /// </summary>
    bool TryValidate(string secret, string code, DateTimeOffset nowUtc, long lastUsedTimeStep, out long timeStep);

    /// <summary>Builds the <c>otpauth://</c> URI an authenticator app scans.</summary>
    string BuildProvisioningUri(string secret, string issuer, string accountName);
}

/// <summary>
/// Sends transactional mail (verification, password reset). A provider is not configured yet, so
/// the default implementation records what would have been sent; the port keeps that fact from
/// leaking into the use cases (deliverable §23).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>The permissions a user holds, resolved for one branch context.</summary>
/// <param name="UserId">Whose permissions these are.</param>
/// <param name="PermissionsVersion">Version they were resolved at, for cache invalidation.</param>
/// <param name="BranchId">Branch the resolution applies to, or null for organization scope.</param>
/// <param name="Codes">Effective permission codes.</param>
/// <param name="RoleNames">Roles that contributed, for display and audit.</param>
public sealed record PermissionSet(
    Guid UserId,
    int PermissionsVersion,
    Guid? BranchId,
    IReadOnlySet<string> Codes,
    IReadOnlyCollection<string> RoleNames)
{
    public bool Allows(string permissionCode) => Codes.Contains(permissionCode);
}

/// <summary>
/// Resolves effective permissions from roles and per-branch overrides (ADR-0013). Always
/// server-side: a client-supplied permission list is decoration, not authorization.
/// </summary>
public interface IPermissionResolver
{
    Task<PermissionSet> ResolveAsync(Guid userId, Guid? branchId, CancellationToken cancellationToken = default);

    /// <summary>Drops cached sets for a user after their roles or grants change.</summary>
    Task InvalidateAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The small amount of user state every authenticated request needs: is the account still active,
/// does the token's security stamp still match, and which branches may it act in.
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="OrganizationId">Their tenant.</param>
/// <param name="SecurityStamp">Current stamp; a mismatch invalidates the token.</param>
/// <param name="PermissionsVersion">Current permission-set version.</param>
/// <param name="IsActive">Whether the account is enabled.</param>
/// <param name="IsLockedOut">Whether a lockout is in force.</param>
/// <param name="IsPlatformOperator">Whether cross-tenant endpoints are available.</param>
/// <param name="BranchIds">Branches the user has been granted.</param>
public sealed record UserSecurityState(
    Guid UserId,
    Guid OrganizationId,
    string SecurityStamp,
    int PermissionsVersion,
    bool IsActive,
    bool IsLockedOut,
    bool IsPlatformOperator,
    IReadOnlySet<Guid> BranchIds);

/// <summary>
/// Supplies <see cref="UserSecurityState"/> with a short cache, so authenticating a request costs
/// no database round trip in the common case while a revocation still takes effect within seconds
/// rather than at token expiry (ADR-0027).
/// </summary>
public interface IUserSecurityStateProvider
{
    Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task InvalidateAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Authentication and token settings, bound from the <c>Jwt</c> and <c>Security</c> sections.</summary>
public sealed class AuthenticationOptions
{
    public const string JwtSection = "Jwt";
    public const string SecuritySection = "Security";

    public string Issuer { get; init; } = "https://localhost:5001";

    public string Audience { get; init; } = "retailflow-clients";

    /// <summary>HS256 for development; RS256 with a key from the environment file in production.</summary>
    public string Algorithm { get; init; } = "HS256";

    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 10;

    public int RefreshTokenDays { get; init; } = 30;

    public int ClockSkewSeconds { get; init; } = 30;

    public int PasswordIterations { get; init; } = 210_000;

    public int MinimumPasswordLength { get; init; } = 8;

    public int MaxFailedAccessAttempts { get; init; } = 5;

    public int LockoutMinutes { get; init; } = 15;

    public int PasswordResetTokenMinutes { get; init; } = 30;

    public int EmailVerificationTokenHours { get; init; } = 48;

    /// <summary>Roles for which MFA is mandatory once the organization enables it.</summary>
    public IReadOnlyList<string> RequireMfaForRoles { get; init; } = [];

    /// <summary>Base64 key used to encrypt MFA secrets at rest.</summary>
    public string SecretProtectionKey { get; init; } = string.Empty;

    public PasswordPolicy ToPasswordPolicy() => new(
        MinimumPasswordLength,
        MaxFailedAccessAttempts,
        TimeSpan.FromMinutes(LockoutMinutes));
}

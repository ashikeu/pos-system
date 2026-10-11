using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Identity;

/// <summary>Why a sign-in attempt was refused, recorded in login history (FR-SEC-08).</summary>
public enum LoginFailureReason
{
    None = 0,
    UnknownUser = 1,
    WrongPassword = 2,
    LockedOut = 3,
    Inactive = 4,
    OrganizationSuspended = 5,
    EmailNotConfirmed = 6,
    MfaRequired = 7,
    MfaInvalid = 8,
    NoBranchAccess = 9,
}

/// <summary>Password and lockout policy, supplied from configuration (ADR-0012, ADR-0023).</summary>
/// <param name="MinimumLength">Shortest allowed password.</param>
/// <param name="MaxFailedAttempts">Failures before the account locks.</param>
/// <param name="LockoutDuration">How long the first lockout lasts.</param>
/// <param name="EscalateLockout">
/// Whether repeated lockouts last longer. A till is a shared machine, so a mistyped password must
/// not punish the shop for an hour, but a password-guessing attempt must get slower.
/// </param>
public sealed record PasswordPolicy(
    int MinimumLength = 8,
    int MaxFailedAttempts = 5,
    TimeSpan? LockoutDuration = null,
    bool EscalateLockout = true)
{
    public TimeSpan Lockout => LockoutDuration ?? TimeSpan.FromMinutes(15);
}

/// <summary>
/// A person who signs in. Users belong to exactly one organization; the same person working for two
/// shops has an account in each, which keeps permissions, lockouts and audit trails per tenant
/// (ADR-0004).
/// </summary>
public sealed class User : TenantEntity
{
    private User()
    {
    }

    private User(Guid id, Guid organizationId, string email, string fullName, string passwordHash)
        : base(id, organizationId)
    {
        Email = email;
        NormalizedEmail = Normalize(email);
        FullName = fullName;
        PasswordHash = passwordHash;
        SecurityStamp = Guid.NewGuid().ToString("N");
        PermissionsVersion = 1;
        IsActive = true;
    }

    public string Email { get; private set; } = string.Empty;

    /// <summary>Upper-invariant form used for lookup, so casing cannot create a duplicate account.</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public string? PhoneNumber { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>
    /// Changes whenever credentials or access change. Access tokens carry it, so a password change
    /// or a deactivation invalidates tokens that have not expired yet (ADR-0027).
    /// </summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    /// <summary>
    /// Incremented when roles or branch grants change, so cached permission sets are discarded
    /// immediately rather than lingering for the life of a token (ADR-0013).
    /// </summary>
    public int PermissionsVersion { get; private set; }

    public bool EmailConfirmed { get; private set; }

    public bool IsActive { get; private set; }

    public bool MustChangePassword { get; private set; }

    public bool MfaEnabled { get; private set; }

    /// <summary>Hashed PIN for fast cashier switching at a terminal. Never the account password.</summary>
    public string? PosPinHash { get; private set; }

    public int AccessFailedCount { get; private set; }

    /// <summary>Consecutive lockouts, used to escalate the lockout window.</summary>
    public int LockoutCount { get; private set; }

    public DateTimeOffset? LockoutEndAtUtc { get; private set; }

    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    public DateTimeOffset? PasswordChangedAtUtc { get; private set; }

    /// <summary>
    /// Marks an account that may act across tenants through explicitly marked endpoints. It still
    /// belongs to an organization — the platform operator's own — so every row stays attributable.
    /// </summary>
    public bool IsPlatformOperator { get; private set; }

    public static User Create(
        Guid id,
        Guid organizationId,
        string email,
        string fullName,
        string passwordHash,
        bool mustChangePassword = false)
    {
        var normalizedEmail = RequireEmail(email);

        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        var user = new User(id, organizationId, normalizedEmail, fullName.Trim(), passwordHash)
        {
            MustChangePassword = mustChangePassword,
        };

        return user;
    }

    public void UpdateProfile(string fullName, string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        FullName = fullName.Trim();
        PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
    }

    public void ChangeEmail(string email)
    {
        Email = RequireEmail(email);
        NormalizedEmail = Normalize(Email);
        EmailConfirmed = false;
        RotateSecurityStamp();
    }

    /// <summary>
    /// Replaces the password hash. Also clears any lockout: whoever just proved they can set the
    /// password does not benefit from the account staying locked.
    /// </summary>
    public void SetPassword(string passwordHash, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
        PasswordChangedAtUtc = nowUtc;
        MustChangePassword = false;
        AccessFailedCount = 0;
        LockoutCount = 0;
        LockoutEndAtUtc = null;
        RotateSecurityStamp();
    }

    /// <summary>
    /// Replaces a hash that was verified with outdated parameters, without touching the security
    /// stamp — the credential has not changed, only how it is stored (ADR-0012).
    /// </summary>
    public void UpgradePasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
    }

    public void RequirePasswordChange() => MustChangePassword = true;

    public void SetPosPin(string? pinHash) =>
        PosPinHash = string.IsNullOrWhiteSpace(pinHash) ? null : pinHash;

    public bool IsLockedOut(DateTimeOffset nowUtc) => LockoutEndAtUtc is { } until && until > nowUtc;

    public void RecordLoginSuccess(DateTimeOffset nowUtc)
    {
        LastLoginAtUtc = nowUtc;
        AccessFailedCount = 0;
        LockoutCount = 0;
        LockoutEndAtUtc = null;
    }

    /// <summary>
    /// Counts a failed attempt and locks the account once the policy threshold is reached. Returns
    /// true when this attempt caused the lock.
    /// </summary>
    public bool RecordLoginFailure(DateTimeOffset nowUtc, PasswordPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        AccessFailedCount++;

        if (AccessFailedCount < policy.MaxFailedAttempts)
        {
            return false;
        }

        LockoutCount++;
        AccessFailedCount = 0;

        var duration = policy.EscalateLockout
            ? policy.Lockout * Math.Pow(2, Math.Min(LockoutCount - 1, 5))
            : policy.Lockout;

        LockoutEndAtUtc = nowUtc.Add(duration);
        Raise(new UserLockedOut(Guid.NewGuid(), nowUtc, OrganizationId, Id, Email, LockoutEndAtUtc.Value, LockoutCount));
        return true;
    }

    public void Unlock()
    {
        AccessFailedCount = 0;
        LockoutEndAtUtc = null;
    }

    public void ConfirmEmail() => EmailConfirmed = true;

    public void Activate()
    {
        IsActive = true;
        RotateSecurityStamp();
    }

    /// <summary>
    /// Deactivates the account. The security stamp rotates, so tokens already issued stop working
    /// at the next validation instead of lasting until they expire.
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        RotateSecurityStamp();
    }

    /// <summary>
    /// Turns on the second factor. The security stamp is deliberately left alone: the credential
    /// has not changed, and signing someone out of the session they just used to enrol would be
    /// a confusing way to reward them for improving their security.
    /// </summary>
    public void EnableMfa() => MfaEnabled = true;

    /// <summary>
    /// Turns the second factor off. Also leaves the stamp alone — the handler requires the account
    /// password for this, so the caller has already proved who they are.
    /// </summary>
    public void DisableMfa() => MfaEnabled = false;

    public void MarkAsPlatformOperator() => IsPlatformOperator = true;

    /// <summary>Called when roles or branch grants change, to invalidate cached permissions.</summary>
    public void BumpPermissionsVersion() => PermissionsVersion++;

    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    private static string RequireEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required.", nameof(email));
        }

        var trimmed = email.Trim();

        // Deliberately minimal: a full RFC 5322 check rejects addresses that work in practice.
        // Deliverability is proven by the verification email, not by a regular expression.
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{email}' is not a valid email address.", nameof(email));
        }

        return trimmed;
    }
}

/// <summary>Raised when an account locks itself out, so the owner can be alerted (FR-SEC-09).</summary>
public sealed record UserLockedOut(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid OrganizationId,
    Guid UserId,
    string Email,
    DateTimeOffset LockoutEndAtUtc,
    int LockoutCount) : IDomainEvent
{
    public string EventType => "identity.user-locked-out";
}

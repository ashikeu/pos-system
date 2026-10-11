using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Identity;

/// <summary>
/// A registered POS device. A terminal that has been offline for days must be able to get a fresh
/// access token without a cashier re-entering credentials, so the device holds its own secret —
/// hashed here, revocable per device, and never granting more than the signed-in user has
/// (ADR-0012, FR-SEC-07).
/// </summary>
public sealed class DeviceRegistration : TenantEntity
{
    private DeviceRegistration()
    {
    }

    private DeviceRegistration(
        Guid id,
        Guid organizationId,
        Guid? branchId,
        Guid? terminalId,
        string name,
        string deviceSecretHash,
        string? fingerprint,
        DateTimeOffset registeredAtUtc)
        : base(id, organizationId)
    {
        BranchId = branchId;
        TerminalId = terminalId;
        Name = name;
        DeviceSecretHash = deviceSecretHash;
        Fingerprint = fingerprint;
        RegisteredAtUtc = registeredAtUtc;
        LastSeenAtUtc = registeredAtUtc;
    }

    public Guid? BranchId { get; private set; }

    public Guid? TerminalId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the device secret. The secret is shown once, at registration.</summary>
    public string DeviceSecretHash { get; private set; } = string.Empty;

    /// <summary>Machine fingerprint reported by the app, to spot a cloned registration.</summary>
    public string? Fingerprint { get; private set; }

    public string? AppVersion { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedReason { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static DeviceRegistration Register(
        Guid id,
        Guid organizationId,
        string name,
        string deviceSecretHash,
        DateTimeOffset nowUtc,
        Guid? branchId = null,
        Guid? terminalId = null,
        string? fingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSecretHash);

        return new DeviceRegistration(
            id,
            organizationId,
            branchId,
            terminalId,
            name.Trim(),
            deviceSecretHash,
            fingerprint?.Trim(),
            nowUtc);
    }

    public void RecordHeartbeat(DateTimeOffset nowUtc, string? appVersion)
    {
        if (nowUtc > LastSeenAtUtc)
        {
            LastSeenAtUtc = nowUtc;
        }

        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            AppVersion = appVersion.Trim();
        }
    }

    public void Revoke(DateTimeOffset nowUtc, string reason)
    {
        if (!IsActive)
        {
            return;
        }

        RevokedAtUtc = nowUtc;
        RevokedReason = string.IsNullOrWhiteSpace(reason) ? "revoked" : reason.Trim();
    }
}

/// <summary>
/// A user's TOTP secret, stored encrypted (FR-SEC-06). Enrolment is two steps — issue, then confirm
/// with a code the authenticator produced — so a mis-scanned QR cannot lock someone out of their
/// own account.
/// </summary>
public sealed class MfaSecret : TenantEntity
{
    private MfaSecret()
    {
    }

    private MfaSecret(Guid id, Guid organizationId, Guid userId, string protectedSecret, DateTimeOffset issuedAtUtc)
        : base(id, organizationId)
    {
        UserId = userId;
        ProtectedSecret = protectedSecret;
        IssuedAtUtc = issuedAtUtc;
    }

    public Guid UserId { get; private set; }

    /// <summary>The shared secret, encrypted at rest (never the raw base32 value).</summary>
    public string ProtectedSecret { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    /// <summary>
    /// Last accepted time step. Rejecting a step that was already used stops a replay of a code
    /// captured over the shoulder or from a log.
    /// </summary>
    public long LastUsedTimeStep { get; private set; }

    public bool IsConfirmed => ConfirmedAtUtc is not null && DisabledAtUtc is null;

    public static MfaSecret Issue(Guid id, Guid organizationId, Guid userId, string protectedSecret, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);
        return new MfaSecret(id, organizationId, userId, protectedSecret, nowUtc);
    }

    public void Confirm(DateTimeOffset nowUtc, long timeStep)
    {
        ConfirmedAtUtc = nowUtc;
        DisabledAtUtc = null;
        LastUsedTimeStep = timeStep;
    }

    public void RecordUse(long timeStep)
    {
        if (timeStep > LastUsedTimeStep)
        {
            LastUsedTimeStep = timeStep;
        }
    }

    public void Disable(DateTimeOffset nowUtc) => DisabledAtUtc = nowUtc;
}

/// <summary>What a single-use token authorises.</summary>
public enum UserTokenPurpose
{
    PasswordReset = 0,
    EmailVerification = 1,
}

/// <summary>
/// A single-use, hashed, expiring token for password reset and email verification (FR-SEC-05).
/// Stored as a hash so a leaked database cannot be used to take over accounts, and consumed on
/// first use so a forwarded email cannot be replayed.
/// </summary>
public sealed class UserToken : TenantEntity
{
    private UserToken()
    {
    }

    private UserToken(
        Guid id,
        Guid organizationId,
        Guid userId,
        UserTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        string? requestedByIp)
        : base(id, organizationId)
    {
        UserId = userId;
        Purpose = purpose;
        TokenHash = tokenHash;
        CreatedAtUtcValue = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        RequestedByIp = requestedByIp;
    }

    public Guid UserId { get; private set; }

    public UserTokenPurpose Purpose { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>When the token was issued, independent of the audit stamp.</summary>
    public DateTimeOffset CreatedAtUtcValue { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public string? RequestedByIp { get; private set; }

    public bool IsUsable(DateTimeOffset nowUtc) => ConsumedAtUtc is null && ExpiresAtUtc > nowUtc;

    public static UserToken Issue(
        Guid id,
        Guid organizationId,
        Guid userId,
        UserTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        string? requestedByIp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Lifetime must be positive.");
        }

        return new UserToken(id, organizationId, userId, purpose, tokenHash, nowUtc, nowUtc.Add(lifetime), requestedByIp);
    }

    public void Consume(DateTimeOffset nowUtc)
    {
        if (ConsumedAtUtc is not null)
        {
            throw new DomainException("This token has already been used.");
        }

        ConsumedAtUtc = nowUtc;
    }
}

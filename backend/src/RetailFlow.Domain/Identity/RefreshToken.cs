using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Identity;

/// <summary>
/// A single-use refresh token, stored only as a hash (ADR-0012). Each sign-in starts a family; every
/// refresh consumes one token and issues its child. Presenting a token that was already consumed
/// means either a replay or a stolen token, and the whole family is revoked — the legitimate user is
/// forced to sign in again, which is the right trade for shutting an attacker out.
/// </summary>
public sealed class RefreshToken : TenantEntity
{
    private RefreshToken()
    {
    }

    private RefreshToken(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid familyId,
        Guid sessionId,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        Guid? parentTokenId,
        string? createdByIp,
        Guid? deviceId)
        : base(id, organizationId)
    {
        UserId = userId;
        FamilyId = familyId;
        SessionId = sessionId;
        TokenHash = tokenHash;
        IssuedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ParentTokenId = parentTokenId;
        CreatedByIp = createdByIp;
        DeviceId = deviceId;
    }

    public Guid UserId { get; private set; }

    /// <summary>Links every token descended from one sign-in, so a replay can revoke all of them.</summary>
    public Guid FamilyId { get; private set; }

    public Guid SessionId { get; private set; }

    /// <summary>SHA-256 of the token value. The value itself is never stored (FR-SEC-12).</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public Guid? ParentTokenId { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedReason { get; private set; }

    /// <summary>The token issued in exchange for this one, for tracing a family.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public string? CreatedByIp { get; private set; }

    public Guid? DeviceId { get; private set; }

    public bool IsConsumed => ConsumedAtUtc is not null;

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool IsExpired(DateTimeOffset nowUtc) => ExpiresAtUtc <= nowUtc;

    public bool IsActive(DateTimeOffset nowUtc) => !IsConsumed && !IsRevoked && !IsExpired(nowUtc);

    public static RefreshToken Issue(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid familyId,
        Guid sessionId,
        string tokenHash,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        Guid? parentTokenId = null,
        string? createdByIp = null,
        Guid? deviceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Lifetime must be positive.");
        }

        return new RefreshToken(
            id,
            organizationId,
            userId,
            familyId,
            sessionId,
            tokenHash,
            nowUtc,
            nowUtc.Add(lifetime),
            parentTokenId,
            createdByIp,
            deviceId);
    }

    /// <summary>Marks this token used and records which token replaced it.</summary>
    public void Consume(DateTimeOffset nowUtc, Guid replacedByTokenId)
    {
        if (IsConsumed)
        {
            throw new DomainException("A refresh token cannot be consumed twice.");
        }

        ConsumedAtUtc = nowUtc;
        ReplacedByTokenId = replacedByTokenId;
    }

    public void Revoke(DateTimeOffset nowUtc, string reason)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = nowUtc;
        RevokedReason = string.IsNullOrWhiteSpace(reason) ? "revoked" : reason.Trim();
    }
}

/// <summary>
/// One sign-in on one device, so a user can see where they are signed in and revoke a session they
/// do not recognise (FR-SEC-07).
/// </summary>
public sealed class UserSession : TenantEntity
{
    private UserSession()
    {
    }

    private UserSession(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid refreshFamilyId,
        DateTimeOffset startedAtUtc,
        string? ipAddress,
        string? userAgent,
        Guid? deviceId,
        Guid? terminalId)
        : base(id, organizationId)
    {
        UserId = userId;
        RefreshFamilyId = refreshFamilyId;
        StartedAtUtc = startedAtUtc;
        LastSeenAtUtc = startedAtUtc;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        DeviceId = deviceId;
        TerminalId = terminalId;
    }

    public Guid UserId { get; private set; }

    public Guid RefreshFamilyId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public Guid? TerminalId { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public string? EndedReason { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public bool IsActive => EndedAtUtc is null;

    public static UserSession Start(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid refreshFamilyId,
        DateTimeOffset nowUtc,
        string? ipAddress = null,
        string? userAgent = null,
        Guid? deviceId = null,
        Guid? terminalId = null) =>
        new(id, organizationId, userId, refreshFamilyId, nowUtc, ipAddress, userAgent, deviceId, terminalId);

    public void Touch(DateTimeOffset nowUtc)
    {
        if (nowUtc > LastSeenAtUtc)
        {
            LastSeenAtUtc = nowUtc;
        }
    }

    public void End(DateTimeOffset nowUtc, string reason)
    {
        if (!IsActive)
        {
            return;
        }

        EndedAtUtc = nowUtc;
        EndedReason = string.IsNullOrWhiteSpace(reason) ? "ended" : reason.Trim();
    }
}

/// <summary>
/// Every sign-in attempt, successful or not (FR-SEC-08). Not tenant-owned, because a failed attempt
/// with an unknown organization code still has to be recorded — that is exactly the kind of attempt
/// worth seeing.
/// </summary>
public sealed class LoginAttempt
{
    private LoginAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public Guid? UserId { get; private set; }

    /// <summary>What the caller typed, so repeated attempts on a wrong address are visible.</summary>
    public string EmailAttempted { get; private set; } = string.Empty;

    public string? OrganizationCodeAttempted { get; private set; }

    public bool Succeeded { get; private set; }

    public LoginFailureReason FailureReason { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public Guid? DeviceId { get; private set; }

    public DateTimeOffset AtUtc { get; private set; }

    public static LoginAttempt Create(
        Guid id,
        DateTimeOffset atUtc,
        string emailAttempted,
        bool succeeded,
        LoginFailureReason failureReason,
        Guid? organizationId = null,
        Guid? userId = null,
        string? organizationCodeAttempted = null,
        string? ipAddress = null,
        string? userAgent = null,
        Guid? deviceId = null) =>
        new()
        {
            Id = id,
            AtUtc = atUtc,

            // Bounded: this is unauthenticated input that lands in a stored row.
            EmailAttempted = Truncate(emailAttempted, 256) ?? string.Empty,
            Succeeded = succeeded,
            FailureReason = failureReason,
            OrganizationId = organizationId,
            UserId = userId,
            OrganizationCodeAttempted = Truncate(organizationCodeAttempted, 32),
            IpAddress = Truncate(ipAddress, 64),
            UserAgent = Truncate(userAgent, 512),
            DeviceId = deviceId,
        };

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length > maxLength ? value[..maxLength] : value;
}

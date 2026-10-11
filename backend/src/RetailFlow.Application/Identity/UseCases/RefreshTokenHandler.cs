using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.UseCases;

/// <summary>
/// Exchanges a refresh token for a new pair (FR-SEC-03). Rotation is unconditional and single-use:
/// presenting a token that was already consumed means the value leaked, so the whole family is
/// revoked and the user signs in again. Losing a session is a far smaller problem than letting a
/// copied token keep working.
/// </summary>
public sealed class RefreshTokenHandler : IUseCase<RefreshRequest, AuthenticationResponse>
{
    private readonly IIdentityRepository _repository;
    private readonly ISecureTokenGenerator _tokens;
    private readonly AuthenticationIssuer _issuer;
    private readonly IMutableTenantContext _tenant;
    private readonly IMutableCurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;

    public RefreshTokenHandler(
        IIdentityRepository repository,
        ISecureTokenGenerator tokens,
        AuthenticationIssuer issuer,
        IMutableTenantContext tenant,
        IMutableCurrentUser currentUser,
        IUnitOfWork unitOfWork,
        IEventPublisher events,
        IClock clock,
        IGuidGenerator guids)
    {
        _repository = repository;
        _tokens = tokens;
        _issuer = issuer;
        _tenant = tenant;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _events = events;
        _clock = clock;
        _guids = guids;
    }

    public async Task<Result<AuthenticationResponse>> HandleAsync(
        RefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return IdentityErrors.RefreshTokenInvalid();
        }

        var now = _clock.UtcNow;
        var hash = _tokens.Hash(request.RefreshToken);

        var token = await _repository.FindRefreshTokenByHashAsync(hash, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return IdentityErrors.RefreshTokenInvalid();
        }

        // The token identifies its tenant, so context is established before anything is written.
        _tenant.Set(new TenantScope(token.OrganizationId));

        if (token.IsConsumed)
        {
            return await HandleReuseAsync(token, now, cancellationToken).ConfigureAwait(false);
        }

        if (token.IsRevoked || token.IsExpired(now))
        {
            return IdentityErrors.RefreshTokenInvalid();
        }

        var user = await _repository.FindUserByIdAsync(token.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            return IdentityErrors.AccountInactive();
        }

        if (user.IsLockedOut(now))
        {
            return IdentityErrors.AccountLocked(user.LockoutEndAtUtc!.Value);
        }

        var session = await _repository.FindSessionAsync(token.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !session.IsActive)
        {
            return IdentityErrors.RefreshTokenInvalid();
        }

        var organization = await _repository
            .FindOrganizationByIdAsync(token.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null || !organization.IsActive)
        {
            return IdentityErrors.OrganizationUnavailable();
        }

        var grants = await _repository.GetBranchGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        var branch = AuthenticationIssuer.ResolveBranch(grants, request.BranchId);
        if (branch.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(branch.Error);
        }

        _currentUser.Set(new UserScope(user.Id, user.FullName, user.PermissionsVersion, token.DeviceId));

        var newTokenId = _guids.NewId(now);
        token.Consume(now, newTokenId);

        var response = await _issuer.IssueForExistingSessionAsync(
            user,
            organization,
            grants,
            session,
            token,
            branch.Value,
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        return response;
    }

    private async Task<Result<AuthenticationResponse>> HandleReuseAsync(
        RefreshToken token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var family = await _repository.GetFamilyTokensAsync(token.FamilyId, cancellationToken).ConfigureAwait(false);

        foreach (var member in family)
        {
            member.Revoke(now, "refresh token reuse detected");
        }

        var session = await _repository.FindSessionAsync(token.SessionId, cancellationToken).ConfigureAwait(false);
        session?.End(now, "refresh token reuse detected");

        await _events.PublishAsync(
            new RefreshTokenReuseDetected(
                _guids.NewId(now),
                now,
                token.OrganizationId,
                token.UserId,
                token.FamilyId,
                token.Id),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        return IdentityErrors.RefreshTokenReused();
    }
}

/// <summary>
/// Raised when a consumed refresh token is presented again — the signal that a token value escaped
/// the device it was issued to. Worth an alert, not just a log line (FR-SEC-03).
/// </summary>
public sealed record RefreshTokenReuseDetected(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid OrganizationId,
    Guid UserId,
    Guid FamilyId,
    Guid PresentedTokenId) : IDomainEvent
{
    public string EventType => "identity.refresh-token-reuse-detected";
}

/// <summary>Ends the current session (FR-SEC-04).</summary>
public sealed class LogoutHandler : IUseCase<LogoutRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly ISecureTokenGenerator _tokens;
    private readonly IMutableTenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public LogoutHandler(
        IIdentityRepository repository,
        ISecureTokenGenerator tokens,
        IMutableTenantContext tenant,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _repository = repository;
        _tokens = tokens;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result<bool>> HandleAsync(
        LogoutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            // Logging out is never an error: a client that has lost its token has already achieved
            // what it asked for.
            return true;
        }

        var now = _clock.UtcNow;
        var token = await _repository
            .FindRefreshTokenByHashAsync(_tokens.Hash(request.RefreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return true;
        }

        _tenant.Set(new TenantScope(token.OrganizationId));

        var family = await _repository.GetFamilyTokensAsync(token.FamilyId, cancellationToken).ConfigureAwait(false);
        foreach (var member in family)
        {
            member.Revoke(now, "signed out");
        }

        var session = await _repository.FindSessionAsync(token.SessionId, cancellationToken).ConfigureAwait(false);
        session?.End(now, "signed out");

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>
/// Ends every session of a user, on every device (FR-SEC-04). A manager can do this for someone
/// else with the session-revoke permission; without it, only for themselves.
/// </summary>
public sealed class LogoutAllHandler : IUseCase<LogoutAllRequest, int>
{
    private readonly IIdentityRepository _repository;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserSecurityStateProvider _securityState;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public LogoutAllHandler(
        IIdentityRepository repository,
        ICurrentUser currentUser,
        ITenantContext tenant,
        IUnitOfWork unitOfWork,
        IUserSecurityStateProvider securityState,
        IAuditWriter audit,
        IClock clock)
    {
        _repository = repository;
        _currentUser = currentUser;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _securityState = securityState;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<int>> HandleAsync(
        LogoutAllRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = request.UserId ?? _currentUser.UserId;
        if (userId is null)
        {
            return Error.Forbidden("identity.not_authenticated", "Sign in first.");
        }

        var now = _clock.UtcNow;
        var user = await _repository.FindUserByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound("identity.user_not_found", "That user does not exist.");
        }

        var sessions = await _repository.GetActiveSessionsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var ended = 0;

        foreach (var session in sessions)
        {
            var family = await _repository
                .GetFamilyTokensAsync(session.RefreshFamilyId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var token in family)
            {
                token.Revoke(now, "signed out everywhere");
            }

            session.End(now, "signed out everywhere");
            ended++;
        }

        // Rotating the stamp also invalidates access tokens that have not expired, which is the
        // point of the operation — otherwise a stolen token keeps working for up to ten minutes.
        user.RotateSecurityStamp();

        await _audit.WriteAsync(
            new AuditEntry(
                "identity.sessions_revoked_all",
                nameof(User),
                user.Id,
                Metadata: new Dictionary<string, string>
                {
                    ["sessionsEnded"] = ended.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["requestedBy"] = _currentUser.UserId?.ToString() ?? "system",
                }),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return ended;
    }
}

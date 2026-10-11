using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.UseCases;

/// <summary>
/// Issues a TOTP secret for the signed-in user (FR-SEC-06). Enrolment is two steps: the secret is
/// stored unconfirmed here, and only a code from the authenticator turns MFA on. A one-step flow
/// would lock out anyone whose clock is wrong or who mis-scanned the QR code.
/// </summary>
public sealed class BeginMfaEnrolmentHandler : IUseCase<BeginMfaEnrolmentRequest, MfaEnrolmentResponse>
{
    private readonly IIdentityRepository _repository;
    private readonly ITotpService _totp;
    private readonly ISecretProtector _protector;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly AuthenticationOptions _options;

    public BeginMfaEnrolmentHandler(
        IIdentityRepository repository,
        ITotpService totp,
        ISecretProtector protector,
        ICurrentUser currentUser,
        ITenantContext tenant,
        IUnitOfWork unitOfWork,
        IClock clock,
        IGuidGenerator guids,
        AuthenticationOptions options)
    {
        _repository = repository;
        _totp = totp;
        _protector = protector;
        _currentUser = currentUser;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _guids = guids;
        _options = options;
    }

    public async Task<Result<MfaEnrolmentResponse>> HandleAsync(
        BeginMfaEnrolmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("identity.not_authenticated", "Sign in first.");
        }

        var user = await _repository.FindUserByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound("identity.user_not_found", "That user does not exist.");
        }

        var now = _clock.UtcNow;
        var secret = _totp.CreateSecret();
        var existing = await _repository.FindMfaSecretAsync(user.Id, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.IsConfirmed)
            {
                return Error.Conflict(
                    "identity.mfa_already_enabled",
                    "Two-factor authentication is already on. Turn it off before enrolling again.");
            }

            // Re-enrolling before confirming replaces the unconfirmed secret, so an abandoned
            // attempt cannot be confirmed later by someone who kept the old QR code.
            existing.Disable(now);
        }

        _repository.AddMfaSecret(MfaSecret.Issue(
            _guids.NewId(now),
            _tenant.OrganizationId,
            user.Id,
            _protector.Protect(secret),
            now));

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MfaEnrolmentResponse(
            secret,
            _totp.BuildProvisioningUri(secret, _options.Issuer, user.Email));
    }
}

/// <summary>Confirms enrolment with a code from the authenticator, turning MFA on.</summary>
public sealed class ConfirmMfaHandler : IUseCase<ConfirmMfaRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly ITotpService _totp;
    private readonly ISecretProtector _protector;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserSecurityStateProvider _securityState;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public ConfirmMfaHandler(
        IIdentityRepository repository,
        ITotpService totp,
        ISecretProtector protector,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        IUserSecurityStateProvider securityState,
        IAuditWriter audit,
        IClock clock)
    {
        _repository = repository;
        _totp = totp;
        _protector = protector;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _securityState = securityState;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<bool>> HandleAsync(
        ConfirmMfaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("identity.not_authenticated", "Sign in first.");
        }

        var user = await _repository.FindUserByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        var secret = await _repository.FindMfaSecretAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null || secret is null)
        {
            return Error.NotFound("identity.mfa_not_started", "Start two-factor enrolment first.");
        }

        var now = _clock.UtcNow;
        var plain = _protector.Unprotect(secret.ProtectedSecret);

        if (!_totp.TryValidate(plain, request.Code ?? string.Empty, now, secret.LastUsedTimeStep, out var timeStep))
        {
            return IdentityErrors.MfaInvalid();
        }

        secret.Confirm(now, timeStep);
        user.EnableMfa();

        await _audit.WriteAsync(
            new AuditEntry("identity.mfa_enabled", nameof(User), user.Id),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>
/// Turns MFA off. The password is required: a stolen access token must not be enough to remove the
/// second factor.
/// </summary>
public sealed class DisableMfaHandler : IUseCase<DisableMfaRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserSecurityStateProvider _securityState;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public DisableMfaHandler(
        IIdentityRepository repository,
        IPasswordHasher hasher,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        IUserSecurityStateProvider securityState,
        IAuditWriter audit,
        IClock clock)
    {
        _repository = repository;
        _hasher = hasher;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _securityState = securityState;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<bool>> HandleAsync(
        DisableMfaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("identity.not_authenticated", "Sign in first.");
        }

        var user = await _repository.FindUserByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound("identity.user_not_found", "That user does not exist.");
        }

        if (_hasher.Verify(user.PasswordHash, request.Password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            return Error.Validation("identity.current_password_invalid", "Your password is incorrect.");
        }

        var secret = await _repository.FindMfaSecretAsync(userId, cancellationToken).ConfigureAwait(false);
        secret?.Disable(_clock.UtcNow);
        user.DisableMfa();

        await _audit.WriteAsync(
            new AuditEntry("identity.mfa_disabled", nameof(User), user.Id),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

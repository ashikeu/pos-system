using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.UseCases;

/// <summary>
/// Error codes the identity module returns. They are part of the API contract: the Flutter app
/// branches on them (force a password change, prompt for a code, show a lockout countdown), so they
/// must not change meaning.
/// </summary>
public static class IdentityErrors
{
    public static Error InvalidCredentials() => Error.Validation(
        "identity.invalid_credentials",
        "The email address or password is incorrect.");

    public static Error AccountLocked(DateTimeOffset until) => Error.Forbidden(
        "identity.account_locked",
        $"Too many failed attempts. Try again after {until.UtcDateTime:HH:mm} UTC or ask a manager to unlock the account.");

    public static Error AccountInactive() => Error.Forbidden(
        "identity.account_inactive",
        "This account has been deactivated.");

    public static Error OrganizationUnavailable() => Error.Forbidden(
        "identity.organization_unavailable",
        "This organization is not active. Contact your administrator.");

    public static Error MfaRequired() => Error.Validation(
        "identity.mfa_required",
        "Enter the code from your authenticator app.");

    public static Error MfaInvalid() => Error.Validation(
        "identity.mfa_invalid",
        "That authenticator code is not valid.");

    public static Error NoBranchAccess() => Error.Forbidden(
        "identity.no_branch_access",
        "This account has no branch access. Ask a manager to grant it.");

    public static Error RefreshTokenInvalid() => Error.Validation(
        "identity.refresh_token_invalid",
        "This session has expired. Sign in again.");

    public static Error RefreshTokenReused() => Error.Forbidden(
        "identity.refresh_token_reused",
        "This session was ended for security reasons. Sign in again.");

    public static Error TooManyAttempts() => new(
        "identity.too_many_attempts",
        "Too many sign-in attempts. Wait a moment and try again.",
        ErrorType.RateLimited);

    public static Error PasswordPolicy(string message) => Error.Validation("identity.password_policy", message);

    public static Error TokenInvalid() => Error.Validation(
        "identity.token_invalid",
        "This link is no longer valid. Request a new one.");
}

/// <summary>
/// Signs a user in (FR-SEC-01). The flow is deliberately pedantic about order: tenant, account
/// state, password, MFA, then branch access — and every outcome, success or failure, is written to
/// login history before the result is returned (FR-SEC-08).
/// </summary>
public sealed class LoginHandler : IUseCase<LoginRequest, AuthenticationResponse>
{
    /// <summary>
    /// Failed attempts per address within the window before sign-in is throttled. This is the
    /// per-account guard; the API adds a per-IP limit (FR-SEC-09).
    /// </summary>
    private const int MaxFailuresPerWindow = 10;

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private readonly IIdentityRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITotpService _totp;
    private readonly ISecretProtector _protector;
    private readonly AuthenticationIssuer _issuer;
    private readonly IMutableTenantContext _tenant;
    private readonly IMutableCurrentUser _currentUser;
    private readonly IUserSecurityStateProvider _securityState;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly IRequestMetadata _requestMetadata;
    private readonly AuthenticationOptions _options;

    public LoginHandler(
        IIdentityRepository repository,
        IPasswordHasher passwordHasher,
        ITotpService totp,
        ISecretProtector protector,
        AuthenticationIssuer issuer,
        IMutableTenantContext tenant,
        IMutableCurrentUser currentUser,
        IUserSecurityStateProvider securityState,
        IUnitOfWork unitOfWork,
        IClock clock,
        IGuidGenerator guids,
        IRequestMetadata requestMetadata,
        AuthenticationOptions options)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _totp = totp;
        _protector = protector;
        _issuer = issuer;
        _tenant = tenant;
        _currentUser = currentUser;
        _securityState = securityState;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _guids = guids;
        _requestMetadata = requestMetadata;
        _options = options;
    }

    public async Task<Result<AuthenticationResponse>> HandleAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var normalizedEmail = User.Normalize(request.Email ?? string.Empty);

        // Per-account throttle before any password work: hashing is intentionally slow, so an
        // unthrottled endpoint is also a cheap way to burn the server's CPU.
        var recentFailures = await _repository
            .CountRecentFailuresAsync(normalizedEmail, now - FailureWindow, cancellationToken)
            .ConfigureAwait(false);

        if (recentFailures >= MaxFailuresPerWindow)
        {
            await RecordAttemptAsync(
                request, normalizedEmail, null, null, false, LoginFailureReason.LockedOut, cancellationToken)
                .ConfigureAwait(false);
            return IdentityErrors.TooManyAttempts();
        }

        var organization = await _repository
            .FindOrganizationByCodeAsync(request.OrganizationCode ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            // An unknown organization code is reported as invalid credentials so the endpoint
            // cannot be used to discover which shops use the platform.
            await RecordAttemptAsync(
                request, normalizedEmail, null, null, false, LoginFailureReason.UnknownUser, cancellationToken)
                .ConfigureAwait(false);
            return IdentityErrors.InvalidCredentials();
        }

        if (!organization.IsActive)
        {
            await RecordAttemptAsync(
                request,
                normalizedEmail,
                organization.OrganizationId,
                null,
                false,
                LoginFailureReason.OrganizationSuspended,
                cancellationToken).ConfigureAwait(false);
            return IdentityErrors.OrganizationUnavailable();
        }

        // From here on the tenant is known, so every write is stamped with it (ADR-0004).
        _tenant.Set(new TenantScope(organization.OrganizationId));

        var user = await _repository
            .FindUserByEmailAsync(organization.OrganizationId, normalizedEmail, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            await RecordAttemptAsync(
                request,
                normalizedEmail,
                organization.OrganizationId,
                null,
                false,
                LoginFailureReason.UnknownUser,
                cancellationToken).ConfigureAwait(false);
            return IdentityErrors.InvalidCredentials();
        }

        if (user.IsLockedOut(now))
        {
            // Lockout is reported plainly rather than hidden behind "invalid credentials". A
            // cashier at a till needs to know to wait or call a manager; the small amount of
            // account-existence information this reveals is worth that.
            await RecordAttemptAsync(
                request, normalizedEmail, organization.OrganizationId, user.Id, false, LoginFailureReason.LockedOut, cancellationToken)
                .ConfigureAwait(false);
            return IdentityErrors.AccountLocked(user.LockoutEndAtUtc!.Value);
        }

        if (!user.IsActive)
        {
            await RecordAttemptAsync(
                request, normalizedEmail, organization.OrganizationId, user.Id, false, LoginFailureReason.Inactive, cancellationToken)
                .ConfigureAwait(false);
            return IdentityErrors.AccountInactive();
        }

        var verification = _passwordHasher.Verify(user.PasswordHash, request.Password ?? string.Empty);

        if (verification == PasswordVerificationResult.Failed)
        {
            var locked = user.RecordLoginFailure(now, _options.ToPasswordPolicy());

            await RecordAttemptAsync(
                request, normalizedEmail, organization.OrganizationId, user.Id, false, LoginFailureReason.WrongPassword, cancellationToken)
                .ConfigureAwait(false);

            await PersistAsync(cancellationToken).ConfigureAwait(false);
            await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

            return locked
                ? IdentityErrors.AccountLocked(user.LockoutEndAtUtc!.Value)
                : IdentityErrors.InvalidCredentials();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The only moment the plaintext is available, so the stored hash is upgraded now.
            user.UpgradePasswordHash(_passwordHasher.Hash(request.Password!));
        }

        var mfaResult = await VerifyMfaAsync(user, request.MfaCode, now, cancellationToken).ConfigureAwait(false);
        if (mfaResult.IsFailure)
        {
            await RecordAttemptAsync(
                request,
                normalizedEmail,
                organization.OrganizationId,
                user.Id,
                false,
                mfaResult.Error.Code == "identity.mfa_required" ? LoginFailureReason.MfaRequired : LoginFailureReason.MfaInvalid,
                cancellationToken).ConfigureAwait(false);

            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthenticationResponse>(mfaResult.Error);
        }

        var grants = await _repository.GetBranchGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        if (grants.Count == 0 && !user.IsPlatformOperator)
        {
            await RecordAttemptAsync(
                request, normalizedEmail, organization.OrganizationId, user.Id, false, LoginFailureReason.NoBranchAccess, cancellationToken)
                .ConfigureAwait(false);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return IdentityErrors.NoBranchAccess();
        }

        var branch = AuthenticationIssuer.ResolveBranch(grants, request.BranchId);
        if (branch.IsFailure)
        {
            await RecordAttemptAsync(
                request, normalizedEmail, organization.OrganizationId, user.Id, false, LoginFailureReason.NoBranchAccess, cancellationToken)
                .ConfigureAwait(false);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthenticationResponse>(branch.Error);
        }

        user.RecordLoginSuccess(now);

        // The audit writer and the save interceptor stamp rows with the acting user, which is only
        // known now that authentication has succeeded.
        _currentUser.Set(new UserScope(user.Id, user.FullName, user.PermissionsVersion, request.DeviceId));

        var response = await _issuer.IssueForNewSessionAsync(
            user,
            organization,
            grants,
            branch.Value,
            request.TerminalId,
            request.DeviceId,
            cancellationToken).ConfigureAwait(false);

        await RecordAttemptAsync(
            request, normalizedEmail, organization.OrganizationId, user.Id, true, LoginFailureReason.None, cancellationToken)
            .ConfigureAwait(false);

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return response;
    }

    private async Task<Result> VerifyMfaAsync(
        User user,
        string? code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!user.MfaEnabled)
        {
            return Result.Success();
        }

        var secret = await _repository.FindMfaSecretAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (secret is null || !secret.IsConfirmed)
        {
            // MfaEnabled without a confirmed secret would lock the user out permanently; treat the
            // account as not enrolled rather than refusing every sign-in.
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure(IdentityErrors.MfaRequired());
        }

        var plainSecret = _protector.Unprotect(secret.ProtectedSecret);

        if (!_totp.TryValidate(plainSecret, code, now, secret.LastUsedTimeStep, out var timeStep))
        {
            return Result.Failure(IdentityErrors.MfaInvalid());
        }

        secret.RecordUse(timeStep);
        return Result.Success();
    }

    private async Task RecordAttemptAsync(
        LoginRequest request,
        string normalizedEmail,
        Guid? organizationId,
        Guid? userId,
        bool succeeded,
        LoginFailureReason reason,
        CancellationToken cancellationToken)
    {
        var attempt = LoginAttempt.Create(
            _guids.NewId(_clock.UtcNow),
            _clock.UtcNow,
            normalizedEmail,
            succeeded,
            reason,
            organizationId,
            userId,
            request.OrganizationCode,
            _requestMetadata.IpAddress,
            _requestMetadata.UserAgent,
            request.DeviceId);

        _repository.AddLoginAttempt(attempt);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Saves inside its own transaction. Sign-in is not idempotent in the
    /// <see cref="IIdempotentRequest"/> sense — each attempt legitimately creates a session or a
    /// history row — so it manages its own boundary rather than carrying a key.
    /// </summary>
    private Task<bool> PersistAsync(CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
}

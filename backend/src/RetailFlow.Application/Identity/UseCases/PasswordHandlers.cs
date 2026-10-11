using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.UseCases;

/// <summary>
/// Password rules, checked in one place so the API, the reset flow and any future admin screen
/// cannot disagree about what is acceptable.
/// </summary>
public static class PasswordRules
{
    /// <summary>
    /// Length plus variety, and a short list of obviously bad choices. Deliberately not a maze of
    /// composition rules: length matters far more, and rules that are hard to satisfy end up on a
    /// sticky note next to the till.
    /// </summary>
    public static Result Validate(string? password, int minimumLength)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return Result.Failure(IdentityErrors.PasswordPolicy("A password is required."));
        }

        if (password.Length < minimumLength)
        {
            return Result.Failure(IdentityErrors.PasswordPolicy(
                $"The password must be at least {minimumLength} characters."));
        }

        if (password.Length > 256)
        {
            // Bounded so a very long value cannot be used to make hashing expensive on purpose.
            return Result.Failure(IdentityErrors.PasswordPolicy("The password is too long."));
        }

        var hasLetter = password.Any(char.IsLetter);
        var hasDigitOrSymbol = password.Any(c => char.IsDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c));

        if (!hasLetter || !hasDigitOrSymbol)
        {
            return Result.Failure(IdentityErrors.PasswordPolicy(
                "The password must contain letters and at least one number or symbol."));
        }

        if (Forbidden.Contains(password))
        {
            return Result.Failure(IdentityErrors.PasswordPolicy("That password is too common. Choose another."));
        }

        return Result.Success();
    }

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "12345678", "123456789", "qwerty123",
        "admin123", "retailflow", "pos12345", "letmein1", "welcome1",
    };
}

/// <summary>Changes the signed-in user's own password (FR-SEC-05).</summary>
public sealed class ChangePasswordHandler : IUseCase<ChangePasswordRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserSecurityStateProvider _securityState;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly AuthenticationOptions _options;

    public ChangePasswordHandler(
        IIdentityRepository repository,
        IPasswordHasher hasher,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        IUserSecurityStateProvider securityState,
        IAuditWriter audit,
        IClock clock,
        AuthenticationOptions options)
    {
        _repository = repository;
        _hasher = hasher;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _securityState = securityState;
        _audit = audit;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<bool>> HandleAsync(
        ChangePasswordRequest request,
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

        if (_hasher.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            return Error.Validation("identity.current_password_invalid", "Your current password is incorrect.");
        }

        var policy = PasswordRules.Validate(request.NewPassword, _options.MinimumPasswordLength);
        if (policy.IsFailure)
        {
            return Result.Failure<bool>(policy.Error);
        }

        if (_hasher.Verify(user.PasswordHash, request.NewPassword!) != PasswordVerificationResult.Failed)
        {
            return Error.Validation(
                "identity.password_unchanged",
                "The new password must be different from the current one.");
        }

        user.SetPassword(_hasher.Hash(request.NewPassword!), _clock.UtcNow);

        await _audit.WriteAsync(
            new AuditEntry("identity.password_changed", nameof(User), user.Id),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

        // Sessions stay alive on purpose: a cashier mid-shift who changes their password should not
        // lose the till. "Sign out everywhere" is a separate, explicit action.
        return true;
    }
}

/// <summary>
/// Starts a password reset (FR-SEC-05). Always reports success, whether or not the address exists:
/// the endpoint is unauthenticated, so a different answer for a known address would turn it into an
/// account-enumeration tool.
/// </summary>
public sealed class ForgotPasswordHandler : IUseCase<ForgotPasswordRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly ISecureTokenGenerator _tokens;
    private readonly IEmailSender _email;
    private readonly IMutableTenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly IRequestMetadata _requestMetadata;
    private readonly AuthenticationOptions _options;

    public ForgotPasswordHandler(
        IIdentityRepository repository,
        ISecureTokenGenerator tokens,
        IEmailSender email,
        IMutableTenantContext tenant,
        IUnitOfWork unitOfWork,
        IClock clock,
        IGuidGenerator guids,
        IRequestMetadata requestMetadata,
        AuthenticationOptions options)
    {
        _repository = repository;
        _tokens = tokens;
        _email = email;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _guids = guids;
        _requestMetadata = requestMetadata;
        _options = options;
    }

    public async Task<Result<bool>> HandleAsync(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var organization = await _repository
            .FindOrganizationByCodeAsync(request.OrganizationCode ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null || !organization.IsActive)
        {
            return true;
        }

        _tenant.Set(new TenantScope(organization.OrganizationId));

        var user = await _repository
            .FindUserByEmailAsync(
                organization.OrganizationId,
                User.Normalize(request.Email ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            return true;
        }

        var now = _clock.UtcNow;
        var tokenValue = _tokens.CreateToken();

        _repository.AddUserToken(UserToken.Issue(
            _guids.NewId(now),
            organization.OrganizationId,
            user.Id,
            UserTokenPurpose.PasswordReset,
            _tokens.Hash(tokenValue),
            now,
            TimeSpan.FromMinutes(_options.PasswordResetTokenMinutes),
            _requestMetadata.IpAddress));

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Sent after the token is committed: an email naming a token that was rolled back would
        // leave the user with a link that never works.
        await _email.SendAsync(
            user.Email,
            "Reset your RetailFlow password",
            $"""
             Hello {user.FullName},

             Use this code to reset your password for {organization.Name}:

             {tokenValue}

             It expires in {_options.PasswordResetTokenMinutes} minutes. If you did not ask for a
             reset, you can ignore this message — your password has not changed.
             """,
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>Completes a password reset with a single-use token (FR-SEC-05).</summary>
public sealed class ResetPasswordHandler : IUseCase<ResetPasswordRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly IPasswordHasher _hasher;
    private readonly ISecureTokenGenerator _tokens;
    private readonly IMutableTenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserSecurityStateProvider _securityState;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly AuthenticationOptions _options;

    public ResetPasswordHandler(
        IIdentityRepository repository,
        IPasswordHasher hasher,
        ISecureTokenGenerator tokens,
        IMutableTenantContext tenant,
        IUnitOfWork unitOfWork,
        IUserSecurityStateProvider securityState,
        IAuditWriter audit,
        IClock clock,
        AuthenticationOptions options)
    {
        _repository = repository;
        _hasher = hasher;
        _tokens = tokens;
        _tenant = tenant;
        _unitOfWork = unitOfWork;
        _securityState = securityState;
        _audit = audit;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<bool>> HandleAsync(
        ResetPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var policy = PasswordRules.Validate(request.NewPassword, _options.MinimumPasswordLength);
        if (policy.IsFailure)
        {
            return Result.Failure<bool>(policy.Error);
        }

        var organization = await _repository
            .FindOrganizationByCodeAsync(request.OrganizationCode ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null || !organization.IsActive)
        {
            return IdentityErrors.TokenInvalid();
        }

        _tenant.Set(new TenantScope(organization.OrganizationId));

        var user = await _repository
            .FindUserByEmailAsync(
                organization.OrganizationId,
                User.Normalize(request.Email ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return IdentityErrors.TokenInvalid();
        }

        var now = _clock.UtcNow;

        var token = await _repository.FindUsableTokenAsync(
            user.Id,
            UserTokenPurpose.PasswordReset,
            _tokens.Hash(request.Token ?? string.Empty),
            now,
            cancellationToken).ConfigureAwait(false);

        if (token is null)
        {
            return IdentityErrors.TokenInvalid();
        }

        token.Consume(now);
        user.SetPassword(_hasher.Hash(request.NewPassword!), now);

        // Every existing session is ended: if the reset was triggered because the account was
        // compromised, leaving the attacker's session alive would defeat the point.
        var sessions = await _repository.GetActiveSessionsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            var family = await _repository
                .GetFamilyTokensAsync(session.RefreshFamilyId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var refreshToken in family)
            {
                refreshToken.Revoke(now, "password reset");
            }

            session.End(now, "password reset");
        }

        await _audit.WriteAsync(
            new AuditEntry("identity.password_reset", nameof(User), user.Id),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _securityState.InvalidateAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>Confirms an email address with a single-use token (FR-SEC-05).</summary>
public sealed class VerifyEmailHandler : IUseCase<VerifyEmailRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly ISecureTokenGenerator _tokens;
    private readonly IMutableTenantContext _tenant;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public VerifyEmailHandler(
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
        VerifyEmailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var organization = await _repository
            .FindOrganizationByCodeAsync(request.OrganizationCode ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (organization is null)
        {
            return IdentityErrors.TokenInvalid();
        }

        _tenant.Set(new TenantScope(organization.OrganizationId));

        var user = await _repository
            .FindUserByEmailAsync(
                organization.OrganizationId,
                User.Normalize(request.Email ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return IdentityErrors.TokenInvalid();
        }

        if (user.EmailConfirmed)
        {
            // Idempotent by nature: clicking the link twice is not an error.
            return true;
        }

        var now = _clock.UtcNow;
        var token = await _repository.FindUsableTokenAsync(
            user.Id,
            UserTokenPurpose.EmailVerification,
            _tokens.Hash(request.Token ?? string.Empty),
            now,
            cancellationToken).ConfigureAwait(false);

        if (token is null)
        {
            return IdentityErrors.TokenInvalid();
        }

        token.Consume(now);
        user.ConfirmEmail();

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

using RetailFlow.Application.Platform.UseCases;

namespace RetailFlow.Application.Identity.Contracts;

/// <summary>
/// Sign-in request. The organization code is required rather than inferred from the email: an
/// address may exist in more than one tenant, and guessing which one — or revealing which tenants
/// an address belongs to — are both worse than asking (ADR-0027).
/// </summary>
/// <param name="OrganizationCode">Short tenant code, as issued at provisioning.</param>
/// <param name="Email">The account's email address.</param>
/// <param name="Password">The password.</param>
/// <param name="BranchId">Branch to act in; defaults to the user's default grant.</param>
/// <param name="TerminalId">POS terminal, when signing in at a till.</param>
/// <param name="DeviceId">Registered device, when the app has one.</param>
/// <param name="MfaCode">TOTP code, required when the account has MFA confirmed.</param>
public sealed record LoginRequest(
    string OrganizationCode,
    string Email,
    string Password,
    Guid? BranchId = null,
    Guid? TerminalId = null,
    Guid? DeviceId = null,
    string? MfaCode = null);

/// <summary>What a client is given after a successful sign-in.</summary>
/// <param name="AccessToken">Short-lived bearer token.</param>
/// <param name="ExpiresInSeconds">Lifetime of the access token.</param>
/// <param name="RefreshToken">Single-use refresh token; rotates on every use.</param>
/// <param name="RefreshTokenExpiresAtUtc">When the refresh token stops working.</param>
/// <param name="User">Who signed in, and what they may do.</param>
public sealed record AuthenticationResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    AuthenticatedUser User);

/// <param name="UserId">The signed-in user.</param>
/// <param name="OrganizationId">Their organization.</param>
/// <param name="OrganizationName">Display name of the organization.</param>
/// <param name="Email">Email address.</param>
/// <param name="FullName">Display name.</param>
/// <param name="SessionId">Session this sign-in created.</param>
/// <param name="BranchId">Branch in scope.</param>
/// <param name="BranchName">Display name of that branch.</param>
/// <param name="Roles">Role names, for the UI.</param>
/// <param name="Permissions">
/// Effective permission codes, so the client can hide what the user cannot do. The server checks
/// them again on every call: hiding a button is not authorization (FR-SEC-10).
/// </param>
/// <param name="PermissionsVersion">Version of that permission set.</param>
/// <param name="MustChangePassword">Whether the client must force a password change now.</param>
/// <param name="MfaEnabled">Whether MFA is confirmed for this account.</param>
/// <param name="AccessibleBranches">Branches the user may switch to.</param>
public sealed record AuthenticatedUser(
    Guid UserId,
    Guid OrganizationId,
    string OrganizationName,
    string Email,
    string FullName,
    Guid SessionId,
    Guid? BranchId,
    string? BranchName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    int PermissionsVersion,
    bool MustChangePassword,
    bool MfaEnabled,
    IReadOnlyCollection<BranchAccess> AccessibleBranches);

/// <param name="BranchId">Branch identifier.</param>
/// <param name="Code">Branch code.</param>
/// <param name="Name">Branch name.</param>
/// <param name="IsDefault">Whether this is the user's default branch.</param>
public sealed record BranchAccess(Guid BranchId, string Code, string Name, bool IsDefault);

/// <param name="RefreshToken">The current refresh token.</param>
/// <param name="BranchId">Optionally switch branch while refreshing.</param>
public sealed record RefreshRequest(string RefreshToken, Guid? BranchId = null);

/// <param name="RefreshToken">Token identifying the session to end.</param>
public sealed record LogoutRequest(string RefreshToken);

/// <param name="UserId">User whose sessions to end. Null means the caller.</param>
public sealed record LogoutAllRequest(Guid? UserId = null);

/// <param name="CurrentPassword">The password being replaced.</param>
/// <param name="NewPassword">The new password.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <param name="OrganizationCode">Tenant the account belongs to.</param>
/// <param name="Email">Address to send the reset link to.</param>
public sealed record ForgotPasswordRequest(string OrganizationCode, string Email);

/// <param name="OrganizationCode">Tenant the account belongs to.</param>
/// <param name="Email">The account's email address.</param>
/// <param name="Token">The token from the reset email.</param>
/// <param name="NewPassword">The new password.</param>
public sealed record ResetPasswordRequest(
    string OrganizationCode,
    string Email,
    string Token,
    string NewPassword);

/// <param name="Token">The token from the verification email.</param>
/// <param name="Email">The address being verified.</param>
/// <param name="OrganizationCode">Tenant the account belongs to.</param>
public sealed record VerifyEmailRequest(string OrganizationCode, string Email, string Token);

/// <summary>Starts MFA enrolment for the signed-in user.</summary>
public sealed record BeginMfaEnrolmentRequest;

/// <param name="Secret">The shared secret, shown once so it can be typed in manually.</param>
/// <param name="ProvisioningUri">The otpauth URI for a QR code.</param>
public sealed record MfaEnrolmentResponse(string Secret, string ProvisioningUri);

/// <param name="Code">A code from the authenticator, proving enrolment worked.</param>
public sealed record ConfirmMfaRequest(string Code);

/// <param name="Password">The account password, to confirm intent.</param>
public sealed record DisableMfaRequest(string Password);

/// <param name="SessionId">Identifier of the session.</param>
/// <param name="StartedAtUtc">When it began.</param>
/// <param name="LastSeenAtUtc">Last activity.</param>
/// <param name="IpAddress">Where from.</param>
/// <param name="UserAgent">Which client.</param>
/// <param name="TerminalId">Terminal, when a POS session.</param>
/// <param name="IsCurrent">Whether this is the session making the request.</param>
public sealed record SessionSummary(
    Guid SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    string? IpAddress,
    string? UserAgent,
    Guid? TerminalId,
    bool IsCurrent);

/// <param name="UserId">Whose sessions to list. Null means the caller's own.</param>
public sealed record ListSessionsRequest(Guid? UserId = null);

/// <param name="SessionId">Session to end.</param>
public sealed record RevokeSessionRequest(Guid SessionId);

/// <param name="AtUtc">When the attempt happened.</param>
/// <param name="Succeeded">Whether it succeeded.</param>
/// <param name="FailureReason">Why it failed, when it did.</param>
/// <param name="IpAddress">Source address.</param>
/// <param name="UserAgent">Client.</param>
public sealed record LoginHistoryEntry(
    DateTimeOffset AtUtc,
    bool Succeeded,
    string? FailureReason,
    string? IpAddress,
    string? UserAgent);

/// <param name="UserId">Whose history to read. Null means the caller's own.</param>
/// <param name="Take">How many entries, newest first.</param>
public sealed record LoginHistoryRequest(Guid? UserId = null, int Take = 50);

/// <param name="Name">Human-readable device name, shown in the device list.</param>
/// <param name="BranchId">Branch the device belongs to.</param>
/// <param name="TerminalId">Terminal the device is installed at.</param>
/// <param name="Fingerprint">Machine fingerprint reported by the app.</param>
/// <param name="IdempotencyKey">Key making repeated registration safe.</param>
public sealed record RegisterDeviceRequest(
    string Name,
    Guid? BranchId,
    Guid? TerminalId,
    string? Fingerprint,
    string IdempotencyKey) : IIdempotentRequest
{
    public int SuccessStatusCode => 201;
}

/// <param name="DeviceId">The registered device.</param>
/// <param name="DeviceSecret">The secret, returned once and never again.</param>
public sealed record RegisterDeviceResponse(Guid DeviceId, string DeviceSecret);

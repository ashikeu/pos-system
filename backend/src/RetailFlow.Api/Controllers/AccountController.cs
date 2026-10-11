using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>
/// What the signed-in user can do to their own account: see who they are, change their password,
/// manage two-factor authentication, review and end their sessions (FR-SEC-04…08).
/// </summary>
[ApiController]
[Route("api/v1/account")]
[Produces("application/json")]
[Authorize]
public sealed class AccountController : ControllerBase
{
    private readonly IUseCase<ChangePasswordRequest, bool> _changePassword;
    private readonly IUseCase<LogoutAllRequest, int> _logoutAll;
    private readonly IUseCase<ListSessionsRequest, IReadOnlyList<SessionSummary>> _listSessions;
    private readonly IUseCase<RevokeSessionRequest, bool> _revokeSession;
    private readonly IUseCase<LoginHistoryRequest, IReadOnlyList<LoginHistoryEntry>> _loginHistory;
    private readonly IUseCase<BeginMfaEnrolmentRequest, MfaEnrolmentResponse> _beginMfa;
    private readonly IUseCase<ConfirmMfaRequest, bool> _confirmMfa;
    private readonly IUseCase<DisableMfaRequest, bool> _disableMfa;
    private readonly IPermissionResolver _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenant;

    public AccountController(
        IUseCase<ChangePasswordRequest, bool> changePassword,
        IUseCase<LogoutAllRequest, int> logoutAll,
        IUseCase<ListSessionsRequest, IReadOnlyList<SessionSummary>> listSessions,
        IUseCase<RevokeSessionRequest, bool> revokeSession,
        IUseCase<LoginHistoryRequest, IReadOnlyList<LoginHistoryEntry>> loginHistory,
        IUseCase<BeginMfaEnrolmentRequest, MfaEnrolmentResponse> beginMfa,
        IUseCase<ConfirmMfaRequest, bool> confirmMfa,
        IUseCase<DisableMfaRequest, bool> disableMfa,
        IPermissionResolver permissions,
        ICurrentUser currentUser,
        ITenantContext tenant)
    {
        _changePassword = changePassword;
        _logoutAll = logoutAll;
        _listSessions = listSessions;
        _revokeSession = revokeSession;
        _loginHistory = loginHistory;
        _beginMfa = beginMfa;
        _confirmMfa = confirmMfa;
        _disableMfa = disableMfa;
        _permissions = permissions;
        _currentUser = currentUser;
        _tenant = tenant;
    }

    /// <summary>
    /// Returns the caller's identity, scope and effective permissions. The client uses this after a
    /// branch switch so its menus match what the server will actually allow.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        var set = await _permissions.ResolveAsync(userId, _tenant.BranchId, cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            userId,
            organizationId = _tenant.OrganizationIdOrNull,
            branchId = _tenant.BranchId,
            terminalId = _tenant.TerminalId,
            userName = _currentUser.UserName,
            permissionsVersion = set.PermissionsVersion,
            roles = set.RoleNames,
            permissions = set.Codes.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
        });
    }

    /// <summary>Changes the caller's own password. Other sessions stay signed in.</summary>
    [HttpPost("password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _changePassword.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Ends every session of the caller, on every device.</summary>
    [HttpPost("logout-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken) =>
        (await _logoutAll.HandleAsync(new LogoutAllRequest(), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists the caller's open sessions.</summary>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<SessionSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions(CancellationToken cancellationToken) =>
        (await _listSessions.HandleAsync(new ListSessionsRequest(), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Ends one of the caller's sessions.</summary>
    [HttpDelete("sessions/{sessionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        var result = await _revokeSession
            .HandleAsync(new RevokeSessionRequest(sessionId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Returns the caller's recent sign-in attempts.</summary>
    [HttpGet("login-history")]
    [ProducesResponseType(typeof(IReadOnlyList<LoginHistoryEntry>), StatusCodes.Status200OK)]
    public async Task<IActionResult> LoginHistory(
        [FromQuery] int take,
        CancellationToken cancellationToken) =>
        (await _loginHistory
            .HandleAsync(new LoginHistoryRequest(Take: take <= 0 ? 50 : take), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Starts two-factor enrolment and returns the secret and QR URI.</summary>
    [HttpPost("mfa")]
    [ProducesResponseType(typeof(MfaEnrolmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BeginMfa(CancellationToken cancellationToken) =>
        (await _beginMfa.HandleAsync(new BeginMfaEnrolmentRequest(), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Confirms enrolment with a code from the authenticator, turning two-factor on.</summary>
    [HttpPost("mfa/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmMfa(ConfirmMfaRequest request, CancellationToken cancellationToken)
    {
        var result = await _confirmMfa.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Turns two-factor off. Requires the account password.</summary>
    [HttpPost("mfa/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DisableMfa(DisableMfaRequest request, CancellationToken cancellationToken)
    {
        var result = await _disableMfa.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }
}

/// <summary>
/// Administration of other users' sessions and devices. Separated from
/// <see cref="AccountController"/> because these actions need permissions, while everything on the
/// account controller is about the caller themselves.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Produces("application/json")]
[Authorize]
public sealed class UserAdministrationController : ControllerBase
{
    private readonly IUseCase<ListSessionsRequest, IReadOnlyList<SessionSummary>> _listSessions;
    private readonly IUseCase<LogoutAllRequest, int> _logoutAll;
    private readonly IUseCase<LoginHistoryRequest, IReadOnlyList<LoginHistoryEntry>> _loginHistory;
    private readonly IUseCase<RegisterDeviceRequest, RegisterDeviceResponse> _registerDevice;

    public UserAdministrationController(
        IUseCase<ListSessionsRequest, IReadOnlyList<SessionSummary>> listSessions,
        IUseCase<LogoutAllRequest, int> logoutAll,
        IUseCase<LoginHistoryRequest, IReadOnlyList<LoginHistoryEntry>> loginHistory,
        IUseCase<RegisterDeviceRequest, RegisterDeviceResponse> registerDevice)
    {
        _listSessions = listSessions;
        _logoutAll = logoutAll;
        _loginHistory = loginHistory;
        _registerDevice = registerDevice;
    }

    /// <summary>Lists another user's open sessions.</summary>
    [HttpGet("{userId:guid}/sessions")]
    [HasPermission(Permissions.Users.ViewSessions)]
    [ProducesResponseType(typeof(IReadOnlyList<SessionSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Sessions(Guid userId, CancellationToken cancellationToken) =>
        (await _listSessions.HandleAsync(new ListSessionsRequest(userId), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Signs another user out everywhere — used when a device is lost or an account is shared.</summary>
    [HttpPost("{userId:guid}/sessions/revoke-all")]
    [HasPermission(Permissions.Users.RevokeSessions)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RevokeAll(Guid userId, CancellationToken cancellationToken) =>
        (await _logoutAll.HandleAsync(new LogoutAllRequest(userId), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Returns another user's sign-in history.</summary>
    [HttpGet("{userId:guid}/login-history")]
    [HasPermission(Permissions.Users.ViewLoginHistory)]
    [ProducesResponseType(typeof(IReadOnlyList<LoginHistoryEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> LoginHistory(
        Guid userId,
        [FromQuery] int take,
        CancellationToken cancellationToken) =>
        (await _loginHistory
            .HandleAsync(new LoginHistoryRequest(userId, take <= 0 ? 50 : take), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Registers a POS device and returns its secret once. Idempotent, so a terminal that retries
    /// after a timeout does not end up with two registrations (ADR-0010).
    /// </summary>
    [HttpPost("devices")]
    [HasPermission(Permissions.Users.ManageDevices)]
    [ProducesResponseType(typeof(RegisterDeviceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RegisterDevice(
        RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _registerDevice.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.UseCases;

namespace RetailFlow.Api.Controllers;

/// <summary>
/// Authentication endpoints (FR-SEC-01…06). Controllers bind, delegate and map: every rule lives in
/// a use case, so the same behaviour applies when the sync engine or a background job signs in.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Authentication)]
public sealed class AuthController : ControllerBase
{
    private readonly IUseCase<LoginRequest, AuthenticationResponse> _login;
    private readonly IUseCase<RefreshRequest, AuthenticationResponse> _refresh;
    private readonly IUseCase<LogoutRequest, bool> _logout;
    private readonly IUseCase<ForgotPasswordRequest, bool> _forgotPassword;
    private readonly IUseCase<ResetPasswordRequest, bool> _resetPassword;
    private readonly IUseCase<VerifyEmailRequest, bool> _verifyEmail;

    public AuthController(
        IUseCase<LoginRequest, AuthenticationResponse> login,
        IUseCase<RefreshRequest, AuthenticationResponse> refresh,
        IUseCase<LogoutRequest, bool> logout,
        IUseCase<ForgotPasswordRequest, bool> forgotPassword,
        IUseCase<ResetPasswordRequest, bool> resetPassword,
        IUseCase<VerifyEmailRequest, bool> verifyEmail)
    {
        _login = login;
        _refresh = refresh;
        _logout = logout;
        _forgotPassword = forgotPassword;
        _resetPassword = resetPassword;
        _verifyEmail = verifyEmail;
    }

    /// <summary>Signs in with an organization code, email and password.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        (await _login.HandleAsync(request, cancellationToken).ConfigureAwait(false)).ToActionResult(this);

    /// <summary>
    /// Exchanges a refresh token for a new pair. The old token stops working immediately; replaying
    /// it ends the whole session (FR-SEC-03).
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        (await _refresh.HandleAsync(request, cancellationToken).ConfigureAwait(false)).ToActionResult(this);

    /// <summary>Ends the session the refresh token belongs to.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        var result = await _logout.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>
    /// Starts a password reset. Always answers 204, whether or not the address exists, so the
    /// endpoint cannot be used to discover accounts.
    /// </summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ForgotPassword(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _forgotPassword.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Completes a password reset with the emailed token.</summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(
        ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _resetPassword.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Confirms an email address.</summary>
    [HttpPost("verify-email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await _verifyEmail.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }
}

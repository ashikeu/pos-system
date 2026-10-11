using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;
using RetailFlow.Infrastructure.Identity.Services;

namespace RetailFlow.Api.Middleware;

/// <summary>
/// Turns a validated token into the ambient tenant and user context the rest of the system reads
/// (ADR-0004, ADR-0027). This is where a request stops being a bag of claims and becomes "this
/// user, in this organization, acting in this branch".
/// </summary>
/// <remarks>
/// Four checks happen here rather than in handlers, because every one of them must apply to every
/// authenticated request: the account still exists and is active, the token's security stamp still
/// matches (so a password change or a sign-out-everywhere takes effect before expiry), the branch
/// asked for is one the user has been granted, and the tenant in the token is the tenant the
/// request will act on.
/// </remarks>
public sealed class AuthenticationContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _branchHeader;

    public AuthenticationContextMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(configuration);

        _next = next;
        _branchHeader = configuration["Api:BranchHeader"] ?? "X-Branch-Id";
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMutableTenantContext tenant,
        IMutableCurrentUser currentUser,
        IUserSecurityStateProvider securityState,
        ILogger<AuthenticationContextMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(securityState);
        ArgumentNullException.ThrowIfNull(logger);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var principal = context.User;

        if (!TryGetGuid(principal, "sub", out var userId)
            || !TryGetGuid(principal, RetailFlowClaims.OrganizationId, out var organizationId))
        {
            await WriteProblemAsync(
                context,
                Error.Forbidden("identity.token_malformed", "The access token is missing required claims."))
                .ConfigureAwait(false);
            return;
        }

        var state = await securityState.GetAsync(userId, context.RequestAborted).ConfigureAwait(false);

        if (state is null || state.OrganizationId != organizationId)
        {
            await WriteProblemAsync(
                context,
                Error.Forbidden("identity.token_invalid", "This session is no longer valid. Sign in again."))
                .ConfigureAwait(false);
            return;
        }

        var tokenStamp = principal.FindFirstValue(RetailFlowClaims.SecurityStamp);
        if (!string.Equals(tokenStamp, state.SecurityStamp, StringComparison.Ordinal))
        {
            // The credential or access changed after this token was issued: a password change, a
            // deactivation, or "sign out everywhere". Not an error the client can fix by retrying.
            AuthLog.StaleSecurityStamp(logger, userId);
            await WriteProblemAsync(
                context,
                Error.Forbidden("identity.session_superseded", "Your session ended. Sign in again."))
                .ConfigureAwait(false);
            return;
        }

        if (!state.IsActive)
        {
            await WriteProblemAsync(
                context,
                Error.Forbidden("identity.account_inactive", "This account has been deactivated."))
                .ConfigureAwait(false);
            return;
        }

        if (state.IsLockedOut)
        {
            await WriteProblemAsync(
                context,
                Error.Forbidden("identity.account_locked", "This account is locked."))
                .ConfigureAwait(false);
            return;
        }

        var branchResult = ResolveBranch(context, principal, state);
        if (branchResult.IsFailure)
        {
            await WriteProblemAsync(context, branchResult.Error).ConfigureAwait(false);
            return;
        }

        TryGetGuid(principal, RetailFlowClaims.TerminalId, out var terminalId);
        TryGetGuid(principal, RetailFlowClaims.DeviceId, out var deviceId);

        tenant.Set(new TenantScope(
            organizationId,
            branchResult.Value,
            terminalId == Guid.Empty ? null : terminalId,
            state.IsPlatformOperator));

        var permissionsVersion = int.TryParse(
            principal.FindFirstValue(RetailFlowClaims.PermissionsVersion),
            CultureInfo.InvariantCulture,
            out var version)
            ? version
            : 0;

        currentUser.Set(new UserScope(
            userId,
            principal.FindFirstValue(ClaimTypes.Name) ?? principal.FindFirstValue("name"),
            permissionsVersion,
            deviceId == Guid.Empty ? null : deviceId));

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the branch the request acts in: the <c>X-Branch-Id</c> header when present, otherwise
    /// the branch the token was issued for. Either way it must be a granted branch — this is the
    /// check that stops a client widening its own scope by editing a header (FR-TEN-03).
    /// </summary>
    private Result<Guid?> ResolveBranch(HttpContext context, ClaimsPrincipal principal, UserSecurityState state)
    {
        var header = context.Request.Headers[_branchHeader].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(header))
        {
            if (!Guid.TryParse(header, out var requested))
            {
                return Error.Validation("identity.branch_header_invalid", $"{_branchHeader} must be a GUID.");
            }

            return state.BranchIds.Contains(requested) || state.IsPlatformOperator
                ? Result.Success<Guid?>(requested)
                : Error.Forbidden("identity.branch_not_granted", "You do not have access to that branch.");
        }

        if (TryGetGuid(principal, RetailFlowClaims.BranchId, out var tokenBranch) && tokenBranch != Guid.Empty)
        {
            // A branch grant removed after the token was issued must stop working immediately.
            return state.BranchIds.Contains(tokenBranch) || state.IsPlatformOperator
                ? Result.Success<Guid?>(tokenBranch)
                : Error.Forbidden("identity.branch_not_granted", "Your access to that branch was removed.");
        }

        return Result.Success<Guid?>(null);
    }

    private static bool TryGetGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        var raw = principal.FindFirstValue(claimType);
        return Guid.TryParse(raw, out value);
    }

    private static async Task WriteProblemAsync(HttpContext context, Error error)
    {
        var problem = ApiProblemDetails.From(error, context);

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }
}

internal static partial class AuthLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Rejected a token for user {UserId} because its security stamp is stale")]
    public static partial void StaleSecurityStamp(ILogger logger, Guid userId);
}

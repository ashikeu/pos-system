using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.UseCases;

/// <summary>
/// Creates the session, the refresh-token family and the access token that a successful sign-in or
/// refresh returns. Shared by both paths so the two can never drift apart — a subtle difference
/// between "what login issues" and "what refresh issues" is how sessions end up unrevocable.
/// </summary>
public sealed class AuthenticationIssuer
{
    private readonly IIdentityRepository _repository;
    private readonly IAccessTokenService _accessTokens;
    private readonly ISecureTokenGenerator _tokens;
    private readonly IPermissionResolver _permissions;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly IRequestMetadata _requestMetadata;
    private readonly AuthenticationOptions _options;

    public AuthenticationIssuer(
        IIdentityRepository repository,
        IAccessTokenService accessTokens,
        ISecureTokenGenerator tokens,
        IPermissionResolver permissions,
        IClock clock,
        IGuidGenerator guids,
        IRequestMetadata requestMetadata,
        AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(requestMetadata);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository;
        _accessTokens = accessTokens;
        _tokens = tokens;
        _permissions = permissions;
        _clock = clock;
        _guids = guids;
        _requestMetadata = requestMetadata;
        _options = options;
    }

    /// <summary>Starts a new session and issues the first token pair of its family.</summary>
    public async Task<AuthenticationResponse> IssueForNewSessionAsync(
        User user,
        OrganizationLookup organization,
        IReadOnlyList<BranchGrant> grants,
        Guid? branchId,
        Guid? terminalId,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(grants);

        var now = _clock.UtcNow;
        var familyId = _guids.NewId(now);

        var session = UserSession.Start(
            _guids.NewId(now),
            user.OrganizationId,
            user.Id,
            familyId,
            now,
            _requestMetadata.IpAddress,
            _requestMetadata.UserAgent,
            deviceId,
            terminalId);

        _repository.AddSession(session);

        return await IssueAsync(
            user,
            organization,
            grants,
            session,
            familyId,
            parentTokenId: null,
            branchId,
            terminalId,
            deviceId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Issues the next token pair in an existing family, keeping the session alive.</summary>
    public async Task<AuthenticationResponse> IssueForExistingSessionAsync(
        User user,
        OrganizationLookup organization,
        IReadOnlyList<BranchGrant> grants,
        UserSession session,
        RefreshToken consumedToken,
        Guid? branchId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(consumedToken);

        session.Touch(_clock.UtcNow);

        return await IssueAsync(
            user,
            organization,
            grants,
            session,
            consumedToken.FamilyId,
            consumedToken.Id,
            branchId,
            session.TerminalId,
            session.DeviceId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the branch a session acts in: the one asked for if it is granted, otherwise the user's
    /// default, otherwise their only branch. Returns a failure when the requested branch is not
    /// granted — a client must not be able to widen its own scope by sending an id (ADR-0004).
    /// </summary>
    public static Result<Guid?> ResolveBranch(IReadOnlyList<BranchGrant> grants, Guid? requestedBranchId)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (requestedBranchId is { } requested)
        {
            return grants.Any(g => g.BranchId == requested)
                ? Result.Success<Guid?>(requested)
                : Error.Forbidden(
                    "identity.branch_not_granted",
                    "You do not have access to the requested branch.");
        }

        var preferred = grants.FirstOrDefault(g => g.IsDefault) ?? (grants.Count > 0 ? grants[0] : null);
        return Result.Success(preferred?.BranchId);
    }

    private async Task<AuthenticationResponse> IssueAsync(
        User user,
        OrganizationLookup organization,
        IReadOnlyList<BranchGrant> grants,
        UserSession session,
        Guid familyId,
        Guid? parentTokenId,
        Guid? branchId,
        Guid? terminalId,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var permissions = await _permissions
            .ResolveAsync(user.Id, branchId, cancellationToken)
            .ConfigureAwait(false);

        var roleNames = permissions.RoleNames.Count > 0
            ? permissions.RoleNames
            : await _repository.GetRoleNamesAsync(user.Id, cancellationToken).ConfigureAwait(false);

        var refreshValue = _tokens.CreateToken();
        var refreshLifetime = TimeSpan.FromDays(_options.RefreshTokenDays);

        var refreshToken = RefreshToken.Issue(
            _guids.NewId(now),
            user.OrganizationId,
            user.Id,
            familyId,
            session.Id,
            _tokens.Hash(refreshValue),
            now,
            refreshLifetime,
            parentTokenId,
            _requestMetadata.IpAddress,
            deviceId);

        _repository.AddRefreshToken(refreshToken);

        var accessToken = _accessTokens.Issue(new AccessTokenRequest(
            user.Id,
            user.OrganizationId,
            session.Id,
            user.SecurityStamp,
            user.PermissionsVersion,
            branchId,
            terminalId,
            deviceId,
            roleNames));

        var branch = branchId is { } selected
            ? grants.FirstOrDefault(g => g.BranchId == selected)
            : null;

        return new AuthenticationResponse(
            accessToken.Value,
            accessToken.ExpiresInSeconds,
            refreshValue,
            refreshToken.ExpiresAtUtc,
            new AuthenticatedUser(
                user.Id,
                user.OrganizationId,
                organization.Name,
                user.Email,
                user.FullName,
                session.Id,
                branchId,
                branch?.Name,
                roleNames,
                permissions.Codes.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                user.PermissionsVersion,
                user.MustChangePassword,
                user.MfaEnabled,
                grants
                    .Select(g => new BranchAccess(g.BranchId, g.Code, g.Name, g.IsDefault))
                    .ToArray()));
    }
}

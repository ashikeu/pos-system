using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.UseCases;

/// <summary>Lists the sessions a user has open, so an unfamiliar one can be ended (FR-SEC-07).</summary>
public sealed class ListSessionsHandler : IUseCase<ListSessionsRequest, IReadOnlyList<SessionSummary>>
{
    private readonly IIdentityRepository _repository;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenant;

    public ListSessionsHandler(
        IIdentityRepository repository,
        ICurrentUser currentUser,
        ITenantContext tenant)
    {
        _repository = repository;
        _currentUser = currentUser;
        _tenant = tenant;
    }

    public async Task<Result<IReadOnlyList<SessionSummary>>> HandleAsync(
        ListSessionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = request.UserId ?? _currentUser.UserId;
        if (userId is null)
        {
            return Error.Forbidden("identity.not_authenticated", "Sign in first.");
        }

        var sessions = await _repository.GetActiveSessionsAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        var summaries = sessions
            .OrderByDescending(s => s.LastSeenAtUtc)
            .Select(s => new SessionSummary(
                s.Id,
                s.StartedAtUtc,
                s.LastSeenAtUtc,
                s.IpAddress,
                s.UserAgent,
                s.TerminalId,
                IsCurrent: false))
            .ToArray();

        return summaries;
    }
}

/// <summary>Ends one session and revokes its refresh-token family (FR-SEC-07).</summary>
public sealed class RevokeSessionHandler : IUseCase<RevokeSessionRequest, bool>
{
    private readonly IIdentityRepository _repository;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public RevokeSessionHandler(
        IIdentityRepository repository,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        IAuditWriter audit,
        IClock clock)
    {
        _repository = repository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<bool>> HandleAsync(
        RevokeSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _repository.FindSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);

        // The tenant filter already limits this to the caller's organization; a session belonging
        // to another tenant simply does not exist from here (ADR-0004).
        if (session is null)
        {
            return Error.NotFound("identity.session_not_found", "That session does not exist.");
        }

        var now = _clock.UtcNow;
        var family = await _repository
            .GetFamilyTokensAsync(session.RefreshFamilyId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in family)
        {
            token.Revoke(now, "session revoked");
        }

        session.End(now, "session revoked");

        await _audit.WriteAsync(
            new AuditEntry(
                "identity.session_revoked",
                nameof(UserSession),
                session.Id,
                Metadata: new Dictionary<string, string>
                {
                    ["targetUserId"] = session.UserId.ToString(),
                    ["requestedBy"] = _currentUser.UserId?.ToString() ?? "system",
                }),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>Returns recent sign-in attempts, successful and failed (FR-SEC-08).</summary>
public sealed class LoginHistoryHandler : IUseCase<LoginHistoryRequest, IReadOnlyList<LoginHistoryEntry>>
{
    private const int MaxTake = 200;

    private readonly IIdentityRepository _repository;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenant;

    public LoginHistoryHandler(
        IIdentityRepository repository,
        ICurrentUser currentUser,
        ITenantContext tenant)
    {
        _repository = repository;
        _currentUser = currentUser;
        _tenant = tenant;
    }

    public async Task<Result<IReadOnlyList<LoginHistoryEntry>>> HandleAsync(
        LoginHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = request.UserId ?? _currentUser.UserId;
        if (userId is null)
        {
            return Error.Forbidden("identity.not_authenticated", "Sign in first.");
        }

        var take = Math.Clamp(request.Take, 1, MaxTake);

        var attempts = await _repository
            .GetLoginHistoryAsync(_tenant.OrganizationId, userId.Value, take, cancellationToken)
            .ConfigureAwait(false);

        return attempts
            .Select(a => new LoginHistoryEntry(
                a.AtUtc,
                a.Succeeded,
                a.FailureReason == LoginFailureReason.None ? null : a.FailureReason.ToString(),
                a.IpAddress,
                a.UserAgent))
            .ToArray();
    }
}

/// <summary>
/// Registers a POS device and returns its secret once (ADR-0012). The secret lets a terminal that
/// has been offline for days obtain a fresh token without a cashier typing credentials at the till.
/// </summary>
public sealed class RegisterDeviceHandler : IUseCase<RegisterDeviceRequest, RegisterDeviceResponse>
{
    private readonly IIdentityRepository _repository;
    private readonly ISecureTokenGenerator _tokens;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;

    public RegisterDeviceHandler(
        IIdentityRepository repository,
        ISecureTokenGenerator tokens,
        ITenantContext tenant,
        IAuditWriter audit,
        IClock clock,
        IGuidGenerator guids)
    {
        _repository = repository;
        _tokens = tokens;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
        _guids = guids;
    }

    public async Task<Result<RegisterDeviceResponse>> HandleAsync(
        RegisterDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error.Validation("identity.device_name_required", "Give the device a name.");
        }

        var now = _clock.UtcNow;
        var secret = _tokens.CreateToken();

        var device = DeviceRegistration.Register(
            _guids.NewId(now),
            _tenant.OrganizationId,
            request.Name,
            _tokens.Hash(secret),
            now,
            request.BranchId,
            request.TerminalId,
            request.Fingerprint);

        _repository.AddDevice(device);

        await _audit.WriteAsync(
            new AuditEntry(
                "identity.device_registered",
                nameof(DeviceRegistration),
                device.Id,
                After: new { device.Name, device.BranchId, device.TerminalId }),
            cancellationToken).ConfigureAwait(false);

        // The secret is returned here and never again; only its hash is stored.
        return new RegisterDeviceResponse(device.Id, secret);
    }
}

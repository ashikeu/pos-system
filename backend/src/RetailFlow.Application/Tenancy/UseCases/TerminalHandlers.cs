using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.UseCases;

/// <summary>Lists POS terminals, optionally for one branch.</summary>
public sealed class ListTerminalsHandler : IUseCase<ListTerminalsRequest, IReadOnlyList<TerminalResponse>>
{
    private readonly ITenancyRepository _repository;

    public ListTerminalsHandler(ITenancyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<TerminalResponse>>> HandleAsync(
        ListTerminalsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var terminals = await _repository
            .GetTerminalsAsync(request.BranchId, request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        return terminals
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .Select(TenancyMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>Creates a POS terminal in a branch, subject to the plan's terminal limit (FR-TEN-05).</summary>
public sealed class CreateTerminalHandler : IUseCase<CreateTerminalRequest, TerminalResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IPlanLimitGuard _limits;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;
    private readonly IClock _clock;

    public CreateTerminalHandler(
        ITenancyRepository repository,
        IPlanLimitGuard limits,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _limits = limits;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
        _clock = clock;
    }

    public async Task<Result<TerminalResponse>> HandleAsync(
        CreateTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capacity = await _limits
            .EnsureCapacityAsync(PlanFeatureCode.PosTerminals, cancellationToken)
            .ConfigureAwait(false);

        if (capacity.IsFailure)
        {
            return capacity.Error;
        }

        var branch = await _repository.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return TenancyErrors.BranchNotFound();
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.TerminalCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return TenancyErrors.TerminalCodeTaken(code);
        }

        var warehouseCheck = await ResolveWarehouseAsync(
            request.DefaultWarehouseId,
            request.BranchId,
            cancellationToken).ConfigureAwait(false);

        if (warehouseCheck.IsFailure)
        {
            return warehouseCheck.Error;
        }

        var terminal = PosTerminal.Create(
            _guids.NewId(_clock.UtcNow),
            _tenant.OrganizationId,
            request.BranchId,
            code,
            request.Name);

        terminal.UpdateDetails(
            request.Name,
            warehouseCheck.Value,
            request.ReceiptTemplate58,
            request.ReceiptTemplate80);

        _repository.AddTerminal(terminal);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.terminal_created",
                nameof(PosTerminal),
                terminal.Id,
                After: new { terminal.Code, terminal.Name, terminal.DefaultWarehouseId },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["branchId"] = request.BranchId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        return TenancyMapping.ToResponse(terminal);
    }

    /// <summary>
    /// Checks that a nominated warehouse exists and belongs to the terminal's branch. A terminal
    /// pointing at another branch's warehouse would post stock movements in the wrong place, and
    /// the mistake would only show up in a stock count weeks later.
    /// </summary>
    private async Task<Result<Guid?>> ResolveWarehouseAsync(
        Guid? warehouseId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        if (warehouseId is not { } id)
        {
            return Result.Success<Guid?>(null);
        }

        var warehouse = await _repository.FindWarehouseAsync(id, cancellationToken).ConfigureAwait(false);

        if (warehouse is null)
        {
            return TenancyErrors.WarehouseNotFound();
        }

        return warehouse.BranchId == branchId
            ? Result.Success<Guid?>(id)
            : TenancyErrors.WarehouseBranchMismatch();
    }
}

/// <summary>Changes a terminal's name, warehouse and receipt templates.</summary>
public sealed class UpdateTerminalHandler : IUseCase<UpdateTerminalRequest, TerminalResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;

    public UpdateTerminalHandler(
        ITenancyRepository repository,
        IEffectiveSettingsProvider settings,
        ITenantContext tenant,
        IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _settings = settings;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<Result<TerminalResponse>> HandleAsync(
        UpdateTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var terminal = await _repository
            .FindTerminalAsync(request.TerminalId, cancellationToken)
            .ConfigureAwait(false);

        if (terminal is null)
        {
            return TenancyErrors.TerminalNotFound();
        }

        if (request.DefaultWarehouseId is { } warehouseId)
        {
            var warehouse = await _repository
                .FindWarehouseAsync(warehouseId, cancellationToken)
                .ConfigureAwait(false);

            if (warehouse is null)
            {
                return TenancyErrors.WarehouseNotFound();
            }

            if (warehouse.BranchId != terminal.BranchId)
            {
                return TenancyErrors.WarehouseBranchMismatch();
            }
        }

        var before = TenancyMapping.ToResponse(terminal);

        terminal.UpdateDetails(
            request.Name,
            request.DefaultWarehouseId,
            request.ReceiptTemplate58,
            request.ReceiptTemplate80);

        var response = TenancyMapping.ToResponse(terminal);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.terminal_updated",
                nameof(PosTerminal),
                terminal.Id,
                Before: before,
                After: response),
            cancellationToken).ConfigureAwait(false);

        // Terminal-level settings sit at the top of the resolution chain (FR-TEN-04).
        await _settings.InvalidateAsync(_tenant.OrganizationId, cancellationToken).ConfigureAwait(false);

        return response;
    }
}

/// <summary>Activates or deactivates a terminal. A deactivated till cannot sell.</summary>
public sealed class SetTerminalActivationHandler : IUseCase<SetTerminalActivationRequest, TerminalResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IAuditWriter _audit;

    public SetTerminalActivationHandler(ITenancyRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<TerminalResponse>> HandleAsync(
        SetTerminalActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var terminal = await _repository
            .FindTerminalAsync(request.TerminalId, cancellationToken)
            .ConfigureAwait(false);

        if (terminal is null)
        {
            return TenancyErrors.TerminalNotFound();
        }

        if (request.IsActive)
        {
            terminal.Activate();
        }
        else
        {
            terminal.Deactivate();
        }

        await _audit.WriteAsync(
            new AuditEntry(
                request.IsActive ? "tenancy.terminal_activated" : "tenancy.terminal_deactivated",
                nameof(PosTerminal),
                terminal.Id,
                After: new { terminal.Code, terminal.IsActive }),
            cancellationToken).ConfigureAwait(false);

        return TenancyMapping.ToResponse(terminal);
    }
}

/// <summary>
/// Binds a device to a terminal and returns its secret once (FR-TEN-01, ADR-0012). The secret is
/// what lets a till that has been offline for days get a fresh token without a cashier typing
/// credentials at the counter.
/// </summary>
public sealed class RegisterTerminalHandler : IUseCase<RegisterTerminalRequest, RegisterTerminalResponse>
{
    private readonly ITenancyRepository _repository;
    private readonly IIdentityRepository _identity;
    private readonly ISecureTokenGenerator _tokens;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;

    public RegisterTerminalHandler(
        ITenancyRepository repository,
        IIdentityRepository identity,
        ISecureTokenGenerator tokens,
        ITenantContext tenant,
        IAuditWriter audit,
        IClock clock,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _identity = identity;
        _tokens = tokens;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
        _guids = guids;
    }

    public async Task<Result<RegisterTerminalResponse>> HandleAsync(
        RegisterTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var terminal = await _repository
            .FindTerminalAsync(request.TerminalId, cancellationToken)
            .ConfigureAwait(false);

        if (terminal is null)
        {
            return TenancyErrors.TerminalNotFound();
        }

        if (!terminal.IsActive)
        {
            return Error.Conflict(
                "tenancy.terminal_inactive",
                "That terminal is deactivated. Activate it before registering a device.");
        }

        var now = _clock.UtcNow;
        var secret = _tokens.CreateToken();

        var device = DeviceRegistration.Register(
            _guids.NewId(now),
            _tenant.OrganizationId,
            string.IsNullOrWhiteSpace(request.DeviceName) ? terminal.Name : request.DeviceName,
            _tokens.Hash(secret),
            now,
            terminal.BranchId,
            terminal.Id,
            request.DeviceFingerprint);

        _identity.AddDevice(device);
        terminal.RecordRegistration(now, request.AppVersion, request.DeviceFingerprint);

        await _audit.WriteAsync(
            new AuditEntry(
                "tenancy.terminal_registered",
                nameof(PosTerminal),
                terminal.Id,
                After: new { terminal.Code, DeviceId = device.Id, request.AppVersion },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["branchId"] = terminal.BranchId.ToString(),
                    ["deviceId"] = device.Id.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        // Returned here and never again; only the hash is stored.
        return new RegisterTerminalResponse(terminal.Id, terminal.BranchId, device.Id, secret);
    }
}

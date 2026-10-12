using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>POS terminals of the caller's organization (FR-TEN-01).</summary>
[ApiController]
[Route("api/v1/terminals")]
[Produces("application/json")]
[Authorize]
public sealed class TerminalsController : ControllerBase
{
    private readonly IUseCase<ListTerminalsRequest, IReadOnlyList<TerminalResponse>> _list;
    private readonly IUseCase<CreateTerminalRequest, TerminalResponse> _create;
    private readonly IUseCase<UpdateTerminalRequest, TerminalResponse> _update;
    private readonly IUseCase<SetTerminalActivationRequest, TerminalResponse> _setActivation;
    private readonly IUseCase<RegisterTerminalRequest, RegisterTerminalResponse> _register;

    public TerminalsController(
        IUseCase<ListTerminalsRequest, IReadOnlyList<TerminalResponse>> list,
        IUseCase<CreateTerminalRequest, TerminalResponse> create,
        IUseCase<UpdateTerminalRequest, TerminalResponse> update,
        IUseCase<SetTerminalActivationRequest, TerminalResponse> setActivation,
        IUseCase<RegisterTerminalRequest, RegisterTerminalResponse> register)
    {
        _list = list;
        _create = create;
        _update = update;
        _setActivation = setActivation;
        _register = register;
    }

    /// <summary>Lists terminals, optionally for one branch.</summary>
    [HttpGet]
    [HasPermission(Permissions.Branches.View)]
    [ProducesResponseType(typeof(IReadOnlyList<TerminalResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _list
            .HandleAsync(new ListTerminalsRequest(branchId, includeInactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a terminal, subject to the plan's terminal limit (FR-TEN-05).</summary>
    [HttpPost]
    [HasPermission(Permissions.Branches.ManageTerminals)]
    [ProducesResponseType(typeof(TerminalResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateTerminalRequest request, CancellationToken cancellationToken)
    {
        var result = await _create.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Changes a terminal's name, warehouse or receipt templates.</summary>
    [HttpPut("{terminalId:guid}")]
    [HasPermission(Permissions.Branches.ManageTerminals)]
    [ProducesResponseType(typeof(TerminalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid terminalId,
        UpdateTerminalRequest request,
        CancellationToken cancellationToken) =>
        (await _update
            .HandleAsync(request with { TerminalId = terminalId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Brings a deactivated terminal back into use.</summary>
    [HttpPost("{terminalId:guid}/activate")]
    [HasPermission(Permissions.Branches.ManageTerminals)]
    [ProducesResponseType(typeof(TerminalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid terminalId, CancellationToken cancellationToken) =>
        (await _setActivation
            .HandleAsync(new SetTerminalActivationRequest(terminalId, IsActive: true), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Stops a terminal selling. Its sales history stays.</summary>
    [HttpPost("{terminalId:guid}/deactivate")]
    [HasPermission(Permissions.Branches.ManageTerminals)]
    [ProducesResponseType(typeof(TerminalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid terminalId, CancellationToken cancellationToken) =>
        (await _setActivation
            .HandleAsync(new SetTerminalActivationRequest(terminalId, IsActive: false), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Binds a device to this terminal and returns the device secret once (ADR-0012). Requires an
    /// <c>Idempotency-Key</c>, so a till that retries after a timeout does not create a second
    /// registration and a second secret.
    /// </summary>
    [HttpPost("{terminalId:guid}/register")]
    [HasPermission(Permissions.Branches.ManageTerminals)]
    [ProducesResponseType(typeof(RegisterTerminalResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(
        Guid terminalId,
        RegisterTerminalRequest request,
        CancellationToken cancellationToken)
    {
        var scoped = request with
        {
            TerminalId = terminalId,
            IdempotencyKey = IdempotencyKeys.Resolve(Request, request.IdempotencyKey),
        };

        var result = await _register.HandleAsync(scoped, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }
}

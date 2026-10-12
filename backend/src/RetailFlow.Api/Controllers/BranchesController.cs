using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>Branches of the caller's organization (FR-TEN-01).</summary>
[ApiController]
[Route("api/v1/branches")]
[Produces("application/json")]
[Authorize]
public sealed class BranchesController : ControllerBase
{
    private readonly IUseCase<ListBranchesRequest, IReadOnlyList<BranchResponse>> _list;
    private readonly IUseCase<GetBranchRequest, BranchResponse> _get;
    private readonly IUseCase<CreateBranchRequest, BranchResponse> _create;
    private readonly IUseCase<UpdateBranchRequest, BranchResponse> _update;
    private readonly IUseCase<SetBranchActivationRequest, BranchResponse> _setActivation;

    public BranchesController(
        IUseCase<ListBranchesRequest, IReadOnlyList<BranchResponse>> list,
        IUseCase<GetBranchRequest, BranchResponse> get,
        IUseCase<CreateBranchRequest, BranchResponse> create,
        IUseCase<UpdateBranchRequest, BranchResponse> update,
        IUseCase<SetBranchActivationRequest, BranchResponse> setActivation)
    {
        _list = list;
        _get = get;
        _create = create;
        _update = update;
        _setActivation = setActivation;
    }

    /// <summary>Lists branches.</summary>
    [HttpGet]
    [HasPermission(Permissions.Branches.View)]
    [ProducesResponseType(typeof(IReadOnlyList<BranchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _list.HandleAsync(new ListBranchesRequest(includeInactive), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Returns one branch.</summary>
    [HttpGet("{branchId:guid}", Name = nameof(GetBranch))]
    [HasPermission(Permissions.Branches.View)]
    [ProducesResponseType(typeof(BranchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBranch(Guid branchId, CancellationToken cancellationToken) =>
        (await _get.HandleAsync(new GetBranchRequest(branchId), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Creates a branch. Subject to the plan's branch limit, and by default it also gets a default
    /// warehouse so it can hold stock immediately (FR-TEN-05).
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.Branches.Create)]
    [ProducesResponseType(typeof(BranchResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        var result = await _create.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? CreatedAtRoute(nameof(GetBranch), new { branchId = result.Value.BranchId }, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Changes a branch's details, time zone or business-day start.</summary>
    [HttpPut("{branchId:guid}")]
    [HasPermission(Permissions.Branches.Update)]
    [ProducesResponseType(typeof(BranchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid branchId,
        UpdateBranchRequest request,
        CancellationToken cancellationToken) =>
        (await _update.HandleAsync(request with { BranchId = branchId }, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Brings a deactivated branch back into use.</summary>
    [HttpPost("{branchId:guid}/activate")]
    [HasPermission(Permissions.Branches.Update)]
    [ProducesResponseType(typeof(BranchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid branchId, CancellationToken cancellationToken) =>
        (await _setActivation
            .HandleAsync(new SetBranchActivationRequest(branchId, IsActive: true), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Stops trading at a branch. Its history stays: deactivation is not deletion, and the last
    /// active branch cannot be deactivated (NFR-09).
    /// </summary>
    [HttpPost("{branchId:guid}/deactivate")]
    [HasPermission(Permissions.Branches.Deactivate)]
    [ProducesResponseType(typeof(BranchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Deactivate(Guid branchId, CancellationToken cancellationToken) =>
        (await _setActivation
            .HandleAsync(new SetBranchActivationRequest(branchId, IsActive: false), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

/// <summary>Stock locations inside the caller's branches (FR-TEN-01).</summary>
[ApiController]
[Route("api/v1/warehouses")]
[Produces("application/json")]
[Authorize]
public sealed class WarehousesController : ControllerBase
{
    private readonly IUseCase<ListWarehousesRequest, IReadOnlyList<WarehouseResponse>> _list;
    private readonly IUseCase<CreateWarehouseRequest, WarehouseResponse> _create;
    private readonly IUseCase<UpdateWarehouseRequest, WarehouseResponse> _update;
    private readonly IUseCase<SetWarehouseActivationRequest, WarehouseResponse> _setActivation;

    public WarehousesController(
        IUseCase<ListWarehousesRequest, IReadOnlyList<WarehouseResponse>> list,
        IUseCase<CreateWarehouseRequest, WarehouseResponse> create,
        IUseCase<UpdateWarehouseRequest, WarehouseResponse> update,
        IUseCase<SetWarehouseActivationRequest, WarehouseResponse> setActivation)
    {
        _list = list;
        _create = create;
        _update = update;
        _setActivation = setActivation;
    }

    /// <summary>Lists warehouses, optionally for one branch.</summary>
    [HttpGet]
    [HasPermission(Permissions.Branches.View)]
    [ProducesResponseType(typeof(IReadOnlyList<WarehouseResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _list
            .HandleAsync(new ListWarehousesRequest(branchId, includeInactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a warehouse in a branch.</summary>
    [HttpPost]
    [HasPermission(Permissions.Branches.ManageWarehouses)]
    [ProducesResponseType(typeof(WarehouseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateWarehouseRequest request, CancellationToken cancellationToken)
    {
        var result = await _create.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Renames a warehouse, and optionally makes it the branch default.</summary>
    [HttpPut("{warehouseId:guid}")]
    [HasPermission(Permissions.Branches.ManageWarehouses)]
    [ProducesResponseType(typeof(WarehouseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid warehouseId,
        UpdateWarehouseRequest request,
        CancellationToken cancellationToken) =>
        (await _update
            .HandleAsync(request with { WarehouseId = warehouseId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Brings a deactivated warehouse back into use.</summary>
    [HttpPost("{warehouseId:guid}/activate")]
    [HasPermission(Permissions.Branches.ManageWarehouses)]
    [ProducesResponseType(typeof(WarehouseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid warehouseId, CancellationToken cancellationToken) =>
        (await _setActivation
            .HandleAsync(new SetWarehouseActivationRequest(warehouseId, IsActive: true), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Takes a warehouse out of use. Refused while it is the branch default, because stock
    /// movements resolve to the default.
    /// </summary>
    [HttpPost("{warehouseId:guid}/deactivate")]
    [HasPermission(Permissions.Branches.ManageWarehouses)]
    [ProducesResponseType(typeof(WarehouseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Deactivate(Guid warehouseId, CancellationToken cancellationToken) =>
        (await _setActivation
            .HandleAsync(new SetWarehouseActivationRequest(warehouseId, IsActive: false), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

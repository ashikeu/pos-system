using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>
/// The caller's own organization and its configuration (FR-TEN-02, FR-TEN-04). Everything here is
/// scoped to the tenant in the token; the organization id is never taken from the request.
/// </summary>
[ApiController]
[Route("api/v1/organization")]
[Produces("application/json")]
[Authorize]
public sealed class OrganizationController : ControllerBase
{
    private readonly IUseCase<GetOrganizationRequest, OrganizationResponse> _get;
    private readonly IUseCase<UpdateOrganizationRequest, OrganizationResponse> _update;
    private readonly IUseCase<GetEffectiveSettingsRequest, EffectiveSettingsResponse> _getSettings;
    private readonly IUseCase<UpdateOrganizationSettingsRequest, EffectiveSettingsResponse> _updateSettings;
    private readonly IUseCase<UpdateBranchSettingsRequest, EffectiveSettingsResponse> _updateBranchSettings;

    public OrganizationController(
        IUseCase<GetOrganizationRequest, OrganizationResponse> get,
        IUseCase<UpdateOrganizationRequest, OrganizationResponse> update,
        IUseCase<GetEffectiveSettingsRequest, EffectiveSettingsResponse> getSettings,
        IUseCase<UpdateOrganizationSettingsRequest, EffectiveSettingsResponse> updateSettings,
        IUseCase<UpdateBranchSettingsRequest, EffectiveSettingsResponse> updateBranchSettings)
    {
        _get = get;
        _update = update;
        _getSettings = getSettings;
        _updateSettings = updateSettings;
        _updateBranchSettings = updateBranchSettings;
    }

    /// <summary>Returns the caller's organization.</summary>
    [HttpGet]
    [HasPermission(Permissions.Organizations.View)]
    [ProducesResponseType(typeof(OrganizationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await _get.HandleAsync(new GetOrganizationRequest(), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Changes the organization's details. The currency cannot be changed (A-01).</summary>
    [HttpPut]
    [HasPermission(Permissions.Organizations.Update)]
    [ProducesResponseType(typeof(OrganizationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken) =>
        (await _update.HandleAsync(request, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Returns settings as they apply to a scope, with each level's overrides alongside the merged
    /// values. Without parameters it answers for the branch and terminal in the caller's token.
    /// </summary>
    [HttpGet("settings")]
    [HasPermission(Permissions.Organizations.View)]
    [ProducesResponseType(typeof(EffectiveSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSettings(
        [FromQuery] Guid? branchId,
        [FromQuery] Guid? terminalId,
        CancellationToken cancellationToken) =>
        (await _getSettings
            .HandleAsync(new GetEffectiveSettingsRequest(branchId, terminalId), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Replaces the organization's settings overrides. A null value clears an override, which puts
    /// that setting back on the platform default.
    /// </summary>
    [HttpPut("settings")]
    [HasPermission(Permissions.Organizations.ManageSettings)]
    [ProducesResponseType(typeof(EffectiveSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateSettings(
        UpdateOrganizationSettingsRequest request,
        CancellationToken cancellationToken) =>
        (await _updateSettings.HandleAsync(request, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Replaces one branch's settings overrides. A null value inherits the organization's.</summary>
    [HttpPut("settings/branches/{branchId:guid}")]
    [HasPermission(Permissions.Organizations.ManageSettings)]
    [ProducesResponseType(typeof(EffectiveSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateBranchSettings(
        Guid branchId,
        UpdateBranchSettingsRequest request,
        CancellationToken cancellationToken)
    {
        // The route is the authority on which branch is being edited; a mismatched body id would
        // otherwise let the path and the effect disagree.
        var scoped = request with { BranchId = branchId };

        return (await _updateBranchSettings.HandleAsync(scoped, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);
    }
}

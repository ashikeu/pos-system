using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Api.Middleware;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>
/// The caller's subscription, its plan and its usage (FR-TEN-05). These endpoints stay available to
/// a suspended or lapsed tenant: they are how it sees what is wrong and moves to a plan that fits
/// (FR-TEN-06).
/// </summary>
[ApiController]
[Route("api/v1/subscription")]
[Produces("application/json")]
[Authorize]
public sealed class SubscriptionController : ControllerBase
{
    private readonly IUseCase<GetSubscriptionRequest, SubscriptionResponse> _get;
    private readonly IUseCase<ListPlansRequest, IReadOnlyList<PlanResponse>> _listPlans;
    private readonly IUseCase<ChangePlanRequest, SubscriptionResponse> _changePlan;

    public SubscriptionController(
        IUseCase<GetSubscriptionRequest, SubscriptionResponse> get,
        IUseCase<ListPlansRequest, IReadOnlyList<PlanResponse>> listPlans,
        IUseCase<ChangePlanRequest, SubscriptionResponse> changePlan)
    {
        _get = get;
        _listPlans = listPlans;
        _changePlan = changePlan;
    }

    /// <summary>Returns the subscription with usage against each of the plan's limits.</summary>
    [HttpGet]
    [HasPermission(Permissions.Organizations.View)]
    [ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await _get.HandleAsync(new GetSubscriptionRequest(), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists the plans on offer.</summary>
    [HttpGet("plans")]
    [HasPermission(Permissions.Organizations.View)]
    [ProducesResponseType(typeof(IReadOnlyList<PlanResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Plans(
        [FromQuery] bool includeWithdrawn,
        CancellationToken cancellationToken) =>
        (await _listPlans
            .HandleAsync(new ListPlansRequest(includeWithdrawn), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Moves to another plan. Refused when the tenant's current usage would already breach the
    /// target plan's limits, with the limit named.
    /// </summary>
    [HttpPost("plan")]
    [HasPermission(Permissions.Organizations.ManageSubscription)]
    [AllowReadOnlyTenant]
    [ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangePlan(ChangePlanRequest request, CancellationToken cancellationToken) =>
        (await _changePlan.HandleAsync(request, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);
}

/// <summary>
/// Cross-tenant administration for whoever runs the platform (ADR-0004, ADR-0030). Every endpoint
/// needs <c>platform.tenants.manage</c>, which no tenant role template grants, and every action is
/// audited with the operator who took it.
/// </summary>
[ApiController]
[Route("api/v1/platform/organizations")]
[Produces("application/json")]
[Authorize]
public sealed class PlatformTenantsController : ControllerBase
{
    private readonly IUseCase<ProvisionOrganizationRequest, ProvisionOrganizationResponse> _provision;
    private readonly IUseCase<SuspendOrganizationRequest, bool> _suspend;
    private readonly IUseCase<ReactivateOrganizationRequest, bool> _reactivate;
    private readonly IUseCase<RenewSubscriptionRequest, bool> _renew;

    public PlatformTenantsController(
        IUseCase<ProvisionOrganizationRequest, ProvisionOrganizationResponse> provision,
        IUseCase<SuspendOrganizationRequest, bool> suspend,
        IUseCase<ReactivateOrganizationRequest, bool> reactivate,
        IUseCase<RenewSubscriptionRequest, bool> renew)
    {
        _provision = provision;
        _suspend = suspend;
        _reactivate = reactivate;
        _renew = renew;
    }

    /// <summary>
    /// Creates a tenant that can trade immediately: settings, a branch with a default warehouse, a
    /// terminal, the role templates, an owner account and a subscription. Requires an
    /// <c>Idempotency-Key</c>: provisioning is slow enough to time out, and a duplicate would mean
    /// two tenants for one customer (ADR-0010).
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.Platform.ManageTenants)]
    [ProducesResponseType(typeof(ProvisionOrganizationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Provision(
        ProvisionOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        var scoped = request with
        {
            IdempotencyKey = IdempotencyKeys.Resolve(Request, request.IdempotencyKey),
        };

        var result = await _provision.HandleAsync(scoped, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>
    /// Blocks a tenant's writes. It can still sign in, read its data and renew — that read-only
    /// surface is the point (FR-TEN-06).
    /// </summary>
    [HttpPost("{organizationId:guid}/suspend")]
    [HasPermission(Permissions.Platform.ManageTenants)]
    [AllowReadOnlyTenant]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Suspend(
        Guid organizationId,
        SuspendOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _suspend
            .HandleAsync(request with { OrganizationId = organizationId }, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Lets a suspended tenant trade again.</summary>
    [HttpPost("{organizationId:guid}/reactivate")]
    [HasPermission(Permissions.Platform.ManageTenants)]
    [AllowReadOnlyTenant]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reactivate(Guid organizationId, CancellationToken cancellationToken)
    {
        var result = await _reactivate
            .HandleAsync(new ReactivateOrganizationRequest(organizationId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }

    /// <summary>Extends a tenant's paid period, which restores its writes.</summary>
    [HttpPost("{organizationId:guid}/subscription/renew")]
    [HasPermission(Permissions.Platform.ManageTenants)]
    [AllowReadOnlyTenant]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Renew(
        Guid organizationId,
        RenewSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _renew
            .HandleAsync(request with { OrganizationId = organizationId }, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }
}

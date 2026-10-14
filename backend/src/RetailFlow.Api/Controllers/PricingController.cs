using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>
/// Tax classes and their effective-dated rate versions (FR-TAX-01…04, ADR-0019). Rates are never
/// edited: a new version is opened from a date, which is what keeps a filed VAT return reproducible.
/// </summary>
[ApiController]
[Route("api/v1/tax")]
[Produces("application/json")]
[Authorize]
public sealed class TaxController : ControllerBase
{
    private readonly IUseCase<ListTaxClassesRequest, IReadOnlyList<TaxClassResponse>> _list;
    private readonly IUseCase<CreateTaxClassRequest, TaxClassResponse> _create;
    private readonly IUseCase<UpdateTaxClassRequest, TaxClassResponse> _update;
    private readonly IUseCase<AddTaxRateVersionRequest, TaxClassResponse> _addVersion;
    private readonly IUseCase<PreviewTaxRequest, PreviewTaxResponse> _preview;

    public TaxController(
        IUseCase<ListTaxClassesRequest, IReadOnlyList<TaxClassResponse>> list,
        IUseCase<CreateTaxClassRequest, TaxClassResponse> create,
        IUseCase<UpdateTaxClassRequest, TaxClassResponse> update,
        IUseCase<AddTaxRateVersionRequest, TaxClassResponse> addVersion,
        IUseCase<PreviewTaxRequest, PreviewTaxResponse> preview)
    {
        _list = list;
        _create = create;
        _update = update;
        _addVersion = addVersion;
        _preview = preview;
    }

    /// <summary>Lists tax classes with their rules and rate versions.</summary>
    [HttpGet("classes")]
    [HasPermission(Permissions.Pricing.View)]
    [ProducesResponseType(typeof(IReadOnlyList<TaxClassResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Classes([FromQuery] bool includeInactive, CancellationToken cancellationToken) =>
        (await _list
            .HandleAsync(new ListTaxClassesRequest(includeInactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a tax class. An exempt or zero-rated class needs a reason for the VAT return.</summary>
    [HttpPost("classes")]
    [HasPermission(Permissions.Pricing.ManageTax)]
    [ProducesResponseType(typeof(TaxClassResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateClass(CreateTaxClassRequest request, CancellationToken cancellationToken)
    {
        var result = await _create.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Changes a tax class's name, treatment or default flag.</summary>
    [HttpPut("classes/{taxClassId:guid}")]
    [HasPermission(Permissions.Pricing.ManageTax)]
    [ProducesResponseType(typeof(TaxClassResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateClass(
        Guid taxClassId,
        UpdateTaxClassRequest request,
        CancellationToken cancellationToken) =>
        (await _update
            .HandleAsync(request with { TaxClassId = taxClassId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Opens a new rate version from a date. Rates are fractions — 15% is <c>0.15</c> — and the
    /// date must be after the version it replaces, because backdating would change what past sales
    /// were taxed at.
    /// </summary>
    [HttpPost("classes/{taxClassId:guid}/versions")]
    [HasPermission(Permissions.Pricing.ManageTax)]
    [ProducesResponseType(typeof(TaxClassResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddVersion(
        Guid taxClassId,
        AddTaxRateVersionRequest request,
        CancellationToken cancellationToken) =>
        (await _addVersion
            .HandleAsync(request with { TaxClassId = taxClassId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Shows what tax would be charged on an amount — the check to run after changing a rate, and
    /// the one support asks for when a receipt looks wrong.
    /// </summary>
    [HttpGet("preview")]
    [HasPermission(Permissions.Pricing.View)]
    [ProducesResponseType(typeof(PreviewTaxResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Preview(
        [FromQuery] decimal amount,
        [FromQuery] Guid? taxClassId = null,
        [FromQuery] decimal quantity = 1m,
        [FromQuery] Guid? branchId = null,
        [FromQuery] DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default) =>
        (await _preview
            .HandleAsync(new PreviewTaxRequest(taxClassId, amount, quantity, branchId, asOfUtc), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

/// <summary>
/// Price lists, branch price overrides, standing discounts, and the resolver that explains which of
/// them won (FR-PRC-01…05, docs/02 §1).
/// </summary>
[ApiController]
[Route("api/v1/pricing")]
[Produces("application/json")]
[Authorize]
public sealed class PricingController : ControllerBase
{
    private readonly IUseCase<ListPriceListsRequest, IReadOnlyList<PriceListResponse>> _listLists;
    private readonly IUseCase<CreatePriceListRequest, PriceListResponse> _createList;
    private readonly IUseCase<SetPriceListItemsRequest, SetPriceListItemsResponse> _setItems;
    private readonly IUseCase<SetBranchPriceRequest, BranchPriceResponse> _setBranchPrice;
    private readonly IUseCase<ListBranchPricesRequest, IReadOnlyList<BranchPriceResponse>> _listBranchPrices;
    private readonly IUseCase<ResolvePriceRequest, ResolvePriceResponse> _resolve;
    private readonly IUseCase<ListDiscountRulesRequest, IReadOnlyList<DiscountRuleResponse>> _listDiscounts;
    private readonly IUseCase<CreateDiscountRuleRequest, DiscountRuleResponse> _createDiscount;
    private readonly IUseCase<SetDiscountRuleActivationRequest, DiscountRuleResponse> _setDiscountActivation;

    public PricingController(
        IUseCase<ListPriceListsRequest, IReadOnlyList<PriceListResponse>> listLists,
        IUseCase<CreatePriceListRequest, PriceListResponse> createList,
        IUseCase<SetPriceListItemsRequest, SetPriceListItemsResponse> setItems,
        IUseCase<SetBranchPriceRequest, BranchPriceResponse> setBranchPrice,
        IUseCase<ListBranchPricesRequest, IReadOnlyList<BranchPriceResponse>> listBranchPrices,
        IUseCase<ResolvePriceRequest, ResolvePriceResponse> resolve,
        IUseCase<ListDiscountRulesRequest, IReadOnlyList<DiscountRuleResponse>> listDiscounts,
        IUseCase<CreateDiscountRuleRequest, DiscountRuleResponse> createDiscount,
        IUseCase<SetDiscountRuleActivationRequest, DiscountRuleResponse> setDiscountActivation)
    {
        _listLists = listLists;
        _createList = createList;
        _setItems = setItems;
        _setBranchPrice = setBranchPrice;
        _listBranchPrices = listBranchPrices;
        _resolve = resolve;
        _listDiscounts = listDiscounts;
        _createDiscount = createDiscount;
        _setDiscountActivation = setDiscountActivation;
    }

    /// <summary>Lists price lists, default first.</summary>
    [HttpGet("lists")]
    [HasPermission(Permissions.Pricing.View)]
    [ProducesResponseType(typeof(IReadOnlyList<PriceListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Lists([FromQuery] bool includeInactive, CancellationToken cancellationToken) =>
        (await _listLists
            .HandleAsync(new ListPriceListsRequest(includeInactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a price list, optionally scoped to a customer group or a branch.</summary>
    [HttpPost("lists")]
    [HasPermission(Permissions.Pricing.Manage)]
    [ProducesResponseType(typeof(PriceListResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateList(CreatePriceListRequest request, CancellationToken cancellationToken)
    {
        var result = await _createList.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>
    /// Sets prices in a list, in a batch. A quantity tier is a separate row, so "20 each, 18 from a
    /// dozen" is two items for the same variant.
    /// </summary>
    [HttpPut("lists/{priceListId:guid}/items")]
    [HasPermission(Permissions.Pricing.Manage)]
    [ProducesResponseType(typeof(SetPriceListItemsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetItems(
        Guid priceListId,
        SetPriceListItemsRequest request,
        CancellationToken cancellationToken) =>
        (await _setItems
            .HandleAsync(request with { PriceListId = priceListId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Sets one branch's price for one variant.</summary>
    [HttpPut("branches/{branchId:guid}/prices")]
    [HasPermission(Permissions.Pricing.Manage)]
    [ProducesResponseType(typeof(BranchPriceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetBranchPrice(
        Guid branchId,
        SetBranchPriceRequest request,
        CancellationToken cancellationToken) =>
        (await _setBranchPrice
            .HandleAsync(request with { BranchId = branchId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists a branch's price overrides.</summary>
    [HttpGet("branches/{branchId:guid}/prices")]
    [HasPermission(Permissions.Pricing.View)]
    [ProducesResponseType(typeof(IReadOnlyList<BranchPriceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BranchPrices(Guid branchId, CancellationToken cancellationToken) =>
        (await _listBranchPrices
            .HandleAsync(new ListBranchPricesRequest(branchId), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Resolves a variant's price and names the source that won — the answer to "why is this
    /// ringing up at that price".
    /// </summary>
    [HttpGet("resolve")]
    [HasPermission(Permissions.Pricing.View)]
    [ProducesResponseType(typeof(ResolvePriceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(
        [FromQuery] Guid variantId,
        [FromQuery] decimal quantity = 1m,
        [FromQuery] Guid? branchId = null,
        [FromQuery] Guid? customerGroupId = null,
        CancellationToken cancellationToken = default) =>
        (await _resolve
            .HandleAsync(new ResolvePriceRequest(variantId, quantity, branchId, customerGroupId), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists standing discount rules.</summary>
    [HttpGet("discounts")]
    [HasPermission(Permissions.Pricing.View)]
    [ProducesResponseType(typeof(IReadOnlyList<DiscountRuleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Discounts(
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _listDiscounts
            .HandleAsync(new ListDiscountRulesRequest(includeInactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a standing discount rule.</summary>
    [HttpPost("discounts")]
    [HasPermission(Permissions.Pricing.ManageDiscounts)]
    [ProducesResponseType(typeof(DiscountRuleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateDiscount(
        CreateDiscountRuleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _createDiscount.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Withdraws a discount rule.</summary>
    [HttpPost("discounts/{discountRuleId:guid}/deactivate")]
    [HasPermission(Permissions.Pricing.ManageDiscounts)]
    [ProducesResponseType(typeof(DiscountRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateDiscount(
        Guid discountRuleId,
        CancellationToken cancellationToken) =>
        (await _setDiscountActivation
            .HandleAsync(new SetDiscountRuleActivationRequest(discountRuleId, false), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Brings a withdrawn discount rule back.</summary>
    [HttpPost("discounts/{discountRuleId:guid}/activate")]
    [HasPermission(Permissions.Pricing.ManageDiscounts)]
    [ProducesResponseType(typeof(DiscountRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateDiscount(
        Guid discountRuleId,
        CancellationToken cancellationToken) =>
        (await _setDiscountActivation
            .HandleAsync(new SetDiscountRuleActivationRequest(discountRuleId, true), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

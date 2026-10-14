using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailFlow.Api.Authorization;
using RetailFlow.Api.Configuration;
using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Identity;

namespace RetailFlow.Api.Controllers;

/// <summary>Products and their variants, barcodes and per-branch stock policy (FR-CAT-01…08).</summary>
[ApiController]
[Route("api/v1/products")]
[Produces("application/json")]
[Authorize]
public sealed class ProductsController : ControllerBase
{
    private readonly IUseCase<SearchProductsRequest, PagedResult<ProductListItem>> _search;
    private readonly IUseCase<GetProductRequest, ProductResponse> _get;
    private readonly IUseCase<CreateProductRequest, ProductResponse> _create;
    private readonly IUseCase<UpdateProductRequest, ProductResponse> _update;
    private readonly IUseCase<SetProductStatusRequest, ProductResponse> _setStatus;
    private readonly IUseCase<AddVariantRequest, ProductResponse> _addVariant;
    private readonly IUseCase<UpdateVariantRequest, ProductResponse> _updateVariant;
    private readonly IUseCase<SetVariantStatusRequest, ProductResponse> _setVariantStatus;
    private readonly IUseCase<AddBarcodeRequest, ProductResponse> _addBarcode;
    private readonly IUseCase<SetProductAttributeRequest, ProductResponse> _setAttribute;
    private readonly IUseCase<SetBranchStockPolicyRequest, BranchStockPolicyResponse> _setBranchPolicy;
    private readonly IUseCase<ListBranchStockPoliciesRequest, IReadOnlyList<BranchStockPolicyResponse>> _listPolicies;

    public ProductsController(
        IUseCase<SearchProductsRequest, PagedResult<ProductListItem>> search,
        IUseCase<GetProductRequest, ProductResponse> get,
        IUseCase<CreateProductRequest, ProductResponse> create,
        IUseCase<UpdateProductRequest, ProductResponse> update,
        IUseCase<SetProductStatusRequest, ProductResponse> setStatus,
        IUseCase<AddVariantRequest, ProductResponse> addVariant,
        IUseCase<UpdateVariantRequest, ProductResponse> updateVariant,
        IUseCase<SetVariantStatusRequest, ProductResponse> setVariantStatus,
        IUseCase<AddBarcodeRequest, ProductResponse> addBarcode,
        IUseCase<SetProductAttributeRequest, ProductResponse> setAttribute,
        IUseCase<SetBranchStockPolicyRequest, BranchStockPolicyResponse> setBranchPolicy,
        IUseCase<ListBranchStockPoliciesRequest, IReadOnlyList<BranchStockPolicyResponse>> listPolicies)
    {
        _search = search;
        _get = get;
        _create = create;
        _update = update;
        _setStatus = setStatus;
        _addVariant = addVariant;
        _updateVariant = updateVariant;
        _setVariantStatus = setVariantStatus;
        _addBarcode = addBarcode;
        _setAttribute = setAttribute;
        _setBranchPolicy = setBranchPolicy;
        _listPolicies = listPolicies;
    }

    /// <summary>
    /// Searches products. <c>search</c> matches a code, SKU or barcode exactly and a name by
    /// prefix; <c>sortBy</c> accepts <c>name</c>, <c>code</c>, <c>status</c> or <c>created</c>.
    /// </summary>
    [HttpGet]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(PagedResult<ProductListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] Guid? categoryId,
        [FromQuery] Guid? brandId,
        [FromQuery] CatalogStatus? status,
        [FromQuery] bool includeArchived,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = PageRequest.DefaultPageSize,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool descending = false,
        CancellationToken cancellationToken = default) =>
        (await _search
            .HandleAsync(
                new SearchProductsRequest(
                    new PageRequest(page, pageSize, sortBy, descending),
                    search,
                    categoryId,
                    brandId,
                    status,
                    includeArchived),
                cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Returns one product with its variants, barcodes and attributes.</summary>
    [HttpGet("{productId:guid}", Name = nameof(GetProduct))]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProduct(Guid productId, CancellationToken cancellationToken) =>
        (await _get.HandleAsync(new GetProductRequest(productId), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Creates a product with its first variant, and a barcode when one is given. Subject to the
    /// plan's product limit (FR-TEN-05).
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.Catalog.Create)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var result = await _create.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? CreatedAtRoute(nameof(GetProduct), new { productId = result.Value.ProductId }, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Changes a product's details, classification and tracking policy.</summary>
    [HttpPut("{productId:guid}")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid productId,
        UpdateProductRequest request,
        CancellationToken cancellationToken) =>
        (await _update.HandleAsync(request with { ProductId = productId }, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Makes a product sellable.</summary>
    [HttpPost("{productId:guid}/activate")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Activate(Guid productId, CancellationToken cancellationToken) =>
        (await _setStatus
            .HandleAsync(new SetProductStatusRequest(productId, CatalogStatus.Active), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Withdraws a product temporarily. Its stock and history stay.</summary>
    [HttpPost("{productId:guid}/deactivate")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Deactivate(Guid productId, CancellationToken cancellationToken) =>
        (await _setStatus
            .HandleAsync(new SetProductStatusRequest(productId, CatalogStatus.Inactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Archives a product for good, with its variants. This is the catalog's delete: sale lines,
    /// stock movements and tax filings reference it, so nothing is removed (NFR-09).
    /// </summary>
    [HttpPost("{productId:guid}/archive")]
    [HasPermission(Permissions.Catalog.Archive)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Archive(Guid productId, CancellationToken cancellationToken) =>
        (await _setStatus
            .HandleAsync(new SetProductStatusRequest(productId, CatalogStatus.Archived), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Adds a variant to a product.</summary>
    [HttpPost("{productId:guid}/variants")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddVariant(
        Guid productId,
        AddVariantRequest request,
        CancellationToken cancellationToken) =>
        (await _addVariant
            .HandleAsync(request with { ProductId = productId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Changes a variant's price, unit, pack size or default flag.</summary>
    [HttpPut("variants/{variantId:guid}")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateVariant(
        Guid variantId,
        UpdateVariantRequest request,
        CancellationToken cancellationToken) =>
        (await _updateVariant
            .HandleAsync(request with { VariantId = variantId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Activates a variant.</summary>
    [HttpPost("variants/{variantId:guid}/activate")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ActivateVariant(Guid variantId, CancellationToken cancellationToken) =>
        (await _setVariantStatus
            .HandleAsync(new SetVariantStatusRequest(variantId, CatalogStatus.Active), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Withdraws a variant. The product's default variant cannot be withdrawn.</summary>
    [HttpPost("variants/{variantId:guid}/deactivate")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeactivateVariant(Guid variantId, CancellationToken cancellationToken) =>
        (await _setVariantStatus
            .HandleAsync(new SetVariantStatusRequest(variantId, CatalogStatus.Inactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Adds a barcode to a variant. The check digit is verified for EAN-13, EAN-8 and UPC-A, and
    /// the code must be unused in this organization.
    /// </summary>
    [HttpPost("variants/{variantId:guid}/barcodes")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddBarcode(
        Guid variantId,
        AddBarcodeRequest request,
        CancellationToken cancellationToken) =>
        (await _addBarcode
            .HandleAsync(request with { VariantId = variantId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Sets a named characteristic on a product.</summary>
    [HttpPut("{productId:guid}/attributes")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetAttribute(
        Guid productId,
        SetProductAttributeRequest request,
        CancellationToken cancellationToken) =>
        (await _setAttribute
            .HandleAsync(request with { ProductId = productId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Sets a variant's stock thresholds at one branch (FR-CAT-08).</summary>
    [HttpPut("variants/{variantId:guid}/branches/{branchId:guid}")]
    [HasPermission(Permissions.Catalog.Update)]
    [ProducesResponseType(typeof(BranchStockPolicyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetBranchPolicy(
        Guid variantId,
        Guid branchId,
        SetBranchStockPolicyRequest request,
        CancellationToken cancellationToken) =>
        (await _setBranchPolicy
            .HandleAsync(request with { VariantId = variantId, BranchId = branchId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists a variant's per-branch stock policies.</summary>
    [HttpGet("variants/{variantId:guid}/branches")]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(IReadOnlyList<BranchStockPolicyResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListBranchPolicies(Guid variantId, CancellationToken cancellationToken) =>
        (await _listPolicies
            .HandleAsync(new ListBranchStockPoliciesRequest(variantId), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

/// <summary>
/// The till's lookup: resolve a scanned code and price it in one call (FR-POS-01). Separate from
/// the admin product endpoints because the permission is different — a cashier may scan but not
/// edit — and because this is the one catalog endpoint on the hot path.
/// </summary>
[ApiController]
[Route("api/v1/catalog")]
[Produces("application/json")]
[Authorize]
public sealed class CatalogLookupController : ControllerBase
{
    private readonly IUseCase<LookupProductRequest, LookupProductResponse> _lookup;

    public CatalogLookupController(IUseCase<LookupProductRequest, LookupProductResponse> lookup) =>
        _lookup = lookup;

    /// <summary>
    /// Resolves a barcode, SKU or product code and returns the product, the resolved price, the tax
    /// snapshot and the line as it would be rung up. A withdrawn product still resolves, with
    /// <c>isSellable: false</c>, so the till can explain itself.
    /// </summary>
    [HttpGet("lookup")]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(LookupProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Lookup(
        [FromQuery] string code,
        [FromQuery] decimal quantity = 1m,
        [FromQuery] Guid? branchId = null,
        [FromQuery] Guid? customerGroupId = null,
        CancellationToken cancellationToken = default) =>
        (await _lookup
            .HandleAsync(new LookupProductRequest(code, quantity, branchId, customerGroupId), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

/// <summary>Categories, brands and units of measure (FR-CAT-04, FR-CAT-07).</summary>
[ApiController]
[Route("api/v1/catalog")]
[Produces("application/json")]
[Authorize]
public sealed class CatalogClassificationController : ControllerBase
{
    private readonly IUseCase<ListCategoriesRequest, IReadOnlyList<CategoryResponse>> _listCategories;
    private readonly IUseCase<CreateCategoryRequest, CategoryResponse> _createCategory;
    private readonly IUseCase<UpdateCategoryRequest, CategoryResponse> _updateCategory;
    private readonly IUseCase<SetCategoryStatusRequest, CategoryResponse> _setCategoryStatus;
    private readonly IUseCase<ListBrandsRequest, IReadOnlyList<BrandResponse>> _listBrands;
    private readonly IUseCase<CreateBrandRequest, BrandResponse> _createBrand;
    private readonly IUseCase<UpdateBrandRequest, BrandResponse> _updateBrand;
    private readonly IUseCase<SetBrandStatusRequest, BrandResponse> _setBrandStatus;
    private readonly IUseCase<ListUomsRequest, IReadOnlyList<UomResponse>> _listUoms;
    private readonly IUseCase<CreateUomRequest, UomResponse> _createUom;
    private readonly IUseCase<ListUomConversionsRequest, IReadOnlyList<UomConversionResponse>> _listConversions;
    private readonly IUseCase<CreateUomConversionRequest, UomConversionResponse> _createConversion;

    public CatalogClassificationController(
        IUseCase<ListCategoriesRequest, IReadOnlyList<CategoryResponse>> listCategories,
        IUseCase<CreateCategoryRequest, CategoryResponse> createCategory,
        IUseCase<UpdateCategoryRequest, CategoryResponse> updateCategory,
        IUseCase<SetCategoryStatusRequest, CategoryResponse> setCategoryStatus,
        IUseCase<ListBrandsRequest, IReadOnlyList<BrandResponse>> listBrands,
        IUseCase<CreateBrandRequest, BrandResponse> createBrand,
        IUseCase<UpdateBrandRequest, BrandResponse> updateBrand,
        IUseCase<SetBrandStatusRequest, BrandResponse> setBrandStatus,
        IUseCase<ListUomsRequest, IReadOnlyList<UomResponse>> listUoms,
        IUseCase<CreateUomRequest, UomResponse> createUom,
        IUseCase<ListUomConversionsRequest, IReadOnlyList<UomConversionResponse>> listConversions,
        IUseCase<CreateUomConversionRequest, UomConversionResponse> createConversion)
    {
        _listCategories = listCategories;
        _createCategory = createCategory;
        _updateCategory = updateCategory;
        _setCategoryStatus = setCategoryStatus;
        _listBrands = listBrands;
        _createBrand = createBrand;
        _updateBrand = updateBrand;
        _setBrandStatus = setBrandStatus;
        _listUoms = listUoms;
        _createUom = createUom;
        _listConversions = listConversions;
        _createConversion = createConversion;
    }

    /// <summary>Lists categories, parents before children.</summary>
    [HttpGet("categories")]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Categories(
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _listCategories
            .HandleAsync(new ListCategoriesRequest(includeInactive), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a category, nested up to four levels deep.</summary>
    [HttpPost("categories")]
    [HasPermission(Permissions.Catalog.ManageCategories)]
    [ProducesResponseType(typeof(CategoryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCategory(
        CreateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _createCategory.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Renames a category or moves it in the tree.</summary>
    [HttpPut("categories/{categoryId:guid}")]
    [HasPermission(Permissions.Catalog.ManageCategories)]
    [ProducesResponseType(typeof(CategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCategory(
        Guid categoryId,
        UpdateCategoryRequest request,
        CancellationToken cancellationToken) =>
        (await _updateCategory
            .HandleAsync(request with { CategoryId = categoryId }, cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Archives a category. Refused while it still has children or products.</summary>
    [HttpPost("categories/{categoryId:guid}/archive")]
    [HasPermission(Permissions.Catalog.ManageCategories)]
    [ProducesResponseType(typeof(CategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ArchiveCategory(Guid categoryId, CancellationToken cancellationToken) =>
        (await _setCategoryStatus
            .HandleAsync(new SetCategoryStatusRequest(categoryId, CatalogStatus.Archived), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists brands.</summary>
    [HttpGet("brands")]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(IReadOnlyList<BrandResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Brands(
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _listBrands.HandleAsync(new ListBrandsRequest(includeInactive), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Creates a brand.</summary>
    [HttpPost("brands")]
    [HasPermission(Permissions.Catalog.ManageCategories)]
    [ProducesResponseType(typeof(BrandResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateBrand(CreateBrandRequest request, CancellationToken cancellationToken)
    {
        var result = await _createBrand.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Renames a brand.</summary>
    [HttpPut("brands/{brandId:guid}")]
    [HasPermission(Permissions.Catalog.ManageCategories)]
    [ProducesResponseType(typeof(BrandResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateBrand(
        Guid brandId,
        UpdateBrandRequest request,
        CancellationToken cancellationToken) =>
        (await _updateBrand.HandleAsync(request with { BrandId = brandId }, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Archives a brand.</summary>
    [HttpPost("brands/{brandId:guid}/archive")]
    [HasPermission(Permissions.Catalog.ManageCategories)]
    [ProducesResponseType(typeof(BrandResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ArchiveBrand(Guid brandId, CancellationToken cancellationToken) =>
        (await _setBrandStatus
            .HandleAsync(new SetBrandStatusRequest(brandId, CatalogStatus.Archived), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Lists units of measure.</summary>
    [HttpGet("units")]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(IReadOnlyList<UomResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Units([FromQuery] bool includeInactive, CancellationToken cancellationToken) =>
        (await _listUoms.HandleAsync(new ListUomsRequest(includeInactive), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>
    /// Creates a unit of measure. <c>decimalPlaces</c> decides whether fractional quantities are
    /// legal — 0 for pieces, 3 for weight.
    /// </summary>
    [HttpPost("units")]
    [HasPermission(Permissions.Catalog.ManageUnits)]
    [ProducesResponseType(typeof(UomResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateUnit(CreateUomRequest request, CancellationToken cancellationToken)
    {
        var result = await _createUom.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.ToActionResult(this);
    }

    /// <summary>Lists unit conversions.</summary>
    [HttpGet("units/conversions")]
    [HasPermission(Permissions.Catalog.View)]
    [ProducesResponseType(typeof(IReadOnlyList<UomConversionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Conversions(CancellationToken cancellationToken) =>
        (await _listConversions.HandleAsync(new ListUomConversionsRequest(), cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Records how many of one unit fit in another. Only within one unit family.</summary>
    [HttpPost("units/conversions")]
    [HasPermission(Permissions.Catalog.ManageUnits)]
    [ProducesResponseType(typeof(UomConversionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateConversion(
        CreateUomConversionRequest request,
        CancellationToken cancellationToken) =>
        (await _createConversion.HandleAsync(request, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);
}

/// <summary>Bulk catalog import and export (FR-CAT-09, FR-CAT-10).</summary>
[ApiController]
[Route("api/v1/catalog")]
[Produces("application/json")]
[Authorize]
public sealed class CatalogImportController : ControllerBase
{
    private readonly IUseCase<ImportProductsRequest, ImportProductsResponse> _import;
    private readonly IUseCase<ExportProductsRequest, ExportProductsResponse> _export;

    public CatalogImportController(
        IUseCase<ImportProductsRequest, ImportProductsResponse> import,
        IUseCase<ExportProductsRequest, ExportProductsResponse> export)
    {
        _import = import;
        _export = export;
    }

    /// <summary>
    /// Imports products from CSV. Validation-only by default: the response reports every bad row
    /// with its row number, and nothing is written until <c>dryRun</c> is false.
    /// </summary>
    [HttpPost("import")]
    [HasPermission(Permissions.Catalog.Import)]
    [ProducesResponseType(typeof(ImportProductsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Import(ImportProductsRequest request, CancellationToken cancellationToken) =>
        (await _import.HandleAsync(request, cancellationToken).ConfigureAwait(false))
            .ToActionResult(this);

    /// <summary>Exports the catalog as CSV, in the shape the importer accepts.</summary>
    [HttpGet("export")]
    [HasPermission(Permissions.Catalog.Export)]
    [ProducesResponseType(typeof(ExportProductsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(
        [FromQuery] bool includeArchived,
        CancellationToken cancellationToken) =>
        (await _export
            .HandleAsync(new ExportProductsRequest(includeArchived), cancellationToken)
            .ConfigureAwait(false))
            .ToActionResult(this);
}

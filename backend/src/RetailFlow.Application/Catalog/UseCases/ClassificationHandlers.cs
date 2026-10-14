using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Catalog.UseCases;

/// <summary>Error codes the catalog module returns; part of the API contract (ADR-0014).</summary>
public static class CatalogErrors
{
    public static Error CategoryNotFound() => Error.NotFound(
        "catalog.category_not_found",
        "That category does not exist.");

    public static Error BrandNotFound() => Error.NotFound("catalog.brand_not_found", "That brand does not exist.");

    public static Error UomNotFound() => Error.NotFound(
        "catalog.uom_not_found",
        "That unit of measure does not exist.");

    public static Error ProductNotFound() => Error.NotFound(
        "catalog.product_not_found",
        "That product does not exist.");

    public static Error VariantNotFound() => Error.NotFound(
        "catalog.variant_not_found",
        "That product variant does not exist.");

    public static Error CodeTaken(string what, string code) => Error.Conflict(
        $"catalog.{what}_code_taken",
        $"The code '{code}' is already used by another {what}.");

    public static Error SkuTaken(string sku) => Error.Conflict(
        "catalog.sku_taken",
        $"SKU '{sku}' is already used by another variant.");

    public static Error BarcodeTaken(string code) => Error.Conflict(
        "catalog.barcode_taken",
        $"Barcode '{code}' is already used in this organization. A barcode must identify one variant.");

    public static Error CategoryInUse() => Error.Conflict(
        "catalog.category_in_use",
        "That category still has child categories or products. Move them first, or deactivate it instead.");

    public static Error ConversionExists() => Error.Conflict(
        "catalog.uom_conversion_exists",
        "A conversion between those units already exists. Change its factor instead.");

    public static Error NotFoundByCode(string code) => Error.NotFound(
        "catalog.product_not_found",
        $"Nothing in the catalog matches '{code}'.");

    /// <summary>Returned when a domain invariant rejects a catalog change.</summary>
    public static Error Invalid(string message) => Error.Validation("catalog.invalid", message);
}

/// <summary>Lists the category tree, parents before children.</summary>
public sealed class ListCategoriesHandler : IUseCase<ListCategoriesRequest, IReadOnlyList<CategoryResponse>>
{
    private readonly ICatalogRepository _repository;

    public ListCategoriesHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<CategoryResponse>>> HandleAsync(
        ListCategoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var categories = await _repository
            .GetCategoriesAsync(request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        return categories
            .OrderBy(c => c.Depth)
            .ThenBy(c => c.SortOrder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CatalogMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>Creates a category, optionally under a parent.</summary>
public sealed class CreateCategoryHandler : IUseCase<CreateCategoryRequest, CategoryResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public CreateCategoryHandler(
        ICatalogRepository repository,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<CategoryResponse>> HandleAsync(
        CreateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.CategoryCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.CodeTaken("category", code);
        }

        Category? parent = null;

        if (request.ParentId is { } parentId)
        {
            parent = await _repository.FindCategoryAsync(parentId, cancellationToken).ConfigureAwait(false);

            if (parent is null)
            {
                return CatalogErrors.CategoryNotFound();
            }
        }

        Category category;

        try
        {
            category = Category.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                code,
                request.Name,
                parent,
                request.SortOrder);
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        category.Rename(request.Name, request.NameBn);
        _repository.AddCategory(category);

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.category_created",
                nameof(Category),
                category.Id,
                After: new { category.Code, category.Name, category.ParentId }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(category);
    }
}

/// <summary>Renames a category and optionally moves it in the tree.</summary>
public sealed class UpdateCategoryHandler : IUseCase<UpdateCategoryRequest, CategoryResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public UpdateCategoryHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<CategoryResponse>> HandleAsync(
        UpdateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var category = await _repository
            .FindCategoryAsync(request.CategoryId, cancellationToken)
            .ConfigureAwait(false);

        if (category is null)
        {
            return CatalogErrors.CategoryNotFound();
        }

        var before = CatalogMapping.ToResponse(category);

        category.Rename(request.Name, request.NameBn);
        category.Reorder(request.SortOrder);

        if (request.ParentId != category.ParentId)
        {
            Category? parent = null;

            if (request.ParentId is { } parentId)
            {
                parent = await _repository.FindCategoryAsync(parentId, cancellationToken).ConfigureAwait(false);

                if (parent is null)
                {
                    return CatalogErrors.CategoryNotFound();
                }
            }

            // The depth limit applies to the whole subtree, and the aggregate cannot see its
            // children, so the height is read here and passed in.
            var height = await _repository
                .GetSubtreeHeightAsync(category.Id, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                category.MoveUnder(parent, height);
            }
            catch (DomainException ex)
            {
                return CatalogErrors.Invalid(ex.Message);
            }
        }

        var after = CatalogMapping.ToResponse(category);

        await _audit.WriteAsync(
            new AuditEntry("catalog.category_updated", nameof(Category), category.Id, before, after),
            cancellationToken).ConfigureAwait(false);

        return after;
    }
}

/// <summary>
/// Moves a category through its lifecycle. Archiving is refused while it still holds children or
/// products: the alternative is products that belong to a category nothing shows.
/// </summary>
public sealed class SetCategoryStatusHandler : IUseCase<SetCategoryStatusRequest, CategoryResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public SetCategoryStatusHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<CategoryResponse>> HandleAsync(
        SetCategoryStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var category = await _repository
            .FindCategoryAsync(request.CategoryId, cancellationToken)
            .ConfigureAwait(false);

        if (category is null)
        {
            return CatalogErrors.CategoryNotFound();
        }

        if (request.Status == CatalogStatus.Archived)
        {
            var hasChildren = await _repository
                .CategoryHasChildrenAsync(category.Id, cancellationToken)
                .ConfigureAwait(false);

            var hasProducts = await _repository
                .CategoryHasProductsAsync(category.Id, cancellationToken)
                .ConfigureAwait(false);

            if (hasChildren || hasProducts)
            {
                return CatalogErrors.CategoryInUse();
            }
        }

        switch (request.Status)
        {
            case CatalogStatus.Active:
                category.Activate();
                break;
            case CatalogStatus.Inactive:
                category.Deactivate();
                break;
            case CatalogStatus.Archived:
                category.Archive();
                break;
            case CatalogStatus.Draft:
            default:
                return CatalogErrors.Invalid("A category cannot be moved back to draft.");
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.category_status_changed",
                nameof(Category),
                category.Id,
                After: new { category.Code, category.Status }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(category);
    }
}

/// <summary>Lists brands.</summary>
public sealed class ListBrandsHandler : IUseCase<ListBrandsRequest, IReadOnlyList<BrandResponse>>
{
    private readonly ICatalogRepository _repository;

    public ListBrandsHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<BrandResponse>>> HandleAsync(
        ListBrandsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var brands = await _repository
            .GetBrandsAsync(request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        return brands
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CatalogMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>Creates a brand.</summary>
public sealed class CreateBrandHandler : IUseCase<CreateBrandRequest, BrandResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public CreateBrandHandler(
        ICatalogRepository repository,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<BrandResponse>> HandleAsync(
        CreateBrandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.BrandCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.CodeTaken("brand", code);
        }

        var brand = Brand.Create(_guids.NewId(), _tenant.OrganizationId, code, request.Name);
        brand.Rename(request.Name, request.NameBn);
        _repository.AddBrand(brand);

        await _audit.WriteAsync(
            new AuditEntry("catalog.brand_created", nameof(Brand), brand.Id, After: new { brand.Code, brand.Name }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(brand);
    }
}

/// <summary>Renames a brand.</summary>
public sealed class UpdateBrandHandler : IUseCase<UpdateBrandRequest, BrandResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public UpdateBrandHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<BrandResponse>> HandleAsync(
        UpdateBrandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var brand = await _repository.FindBrandAsync(request.BrandId, cancellationToken).ConfigureAwait(false);

        if (brand is null)
        {
            return CatalogErrors.BrandNotFound();
        }

        var before = CatalogMapping.ToResponse(brand);
        brand.Rename(request.Name, request.NameBn);
        var after = CatalogMapping.ToResponse(brand);

        await _audit.WriteAsync(
            new AuditEntry("catalog.brand_updated", nameof(Brand), brand.Id, before, after),
            cancellationToken).ConfigureAwait(false);

        return after;
    }
}

/// <summary>Moves a brand through its lifecycle.</summary>
public sealed class SetBrandStatusHandler : IUseCase<SetBrandStatusRequest, BrandResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public SetBrandStatusHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<BrandResponse>> HandleAsync(
        SetBrandStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var brand = await _repository.FindBrandAsync(request.BrandId, cancellationToken).ConfigureAwait(false);

        if (brand is null)
        {
            return CatalogErrors.BrandNotFound();
        }

        switch (request.Status)
        {
            case CatalogStatus.Active:
                brand.Activate();
                break;
            case CatalogStatus.Inactive:
                brand.Deactivate();
                break;
            case CatalogStatus.Archived:
                brand.Archive();
                break;
            case CatalogStatus.Draft:
            default:
                return CatalogErrors.Invalid("A brand cannot be moved back to draft.");
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.brand_status_changed",
                nameof(Brand),
                brand.Id,
                After: new { brand.Code, brand.Status }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(brand);
    }
}

/// <summary>Lists units of measure.</summary>
public sealed class ListUomsHandler : IUseCase<ListUomsRequest, IReadOnlyList<UomResponse>>
{
    private readonly ICatalogRepository _repository;

    public ListUomsHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<UomResponse>>> HandleAsync(
        ListUomsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uoms = await _repository.GetUomsAsync(request.IncludeInactive, cancellationToken).ConfigureAwait(false);

        return uoms
            .OrderBy(u => u.Kind)
            .ThenBy(u => u.Code, StringComparer.Ordinal)
            .Select(CatalogMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>Creates a unit of measure.</summary>
public sealed class CreateUomHandler : IUseCase<CreateUomRequest, UomResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly ITenantContext _tenant;
    private readonly IGuidGenerator _guids;

    public CreateUomHandler(ICatalogRepository repository, ITenantContext tenant, IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenant = tenant;
        _guids = guids;
    }

    public async Task<Result<UomResponse>> HandleAsync(
        CreateUomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.UomCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.CodeTaken("uom", code);
        }

        UnitOfMeasure uom;

        try
        {
            uom = UnitOfMeasure.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                code,
                request.Name,
                request.Kind,
                request.DecimalPlaces);
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        uom.Rename(request.Name, request.NameBn);
        _repository.AddUom(uom);

        return CatalogMapping.ToResponse(uom);
    }
}

/// <summary>Records how many of one unit fit in another.</summary>
public sealed class CreateUomConversionHandler : IUseCase<CreateUomConversionRequest, UomConversionResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly ITenantContext _tenant;
    private readonly IGuidGenerator _guids;

    public CreateUomConversionHandler(ICatalogRepository repository, ITenantContext tenant, IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenant = tenant;
        _guids = guids;
    }

    public async Task<Result<UomConversionResponse>> HandleAsync(
        CreateUomConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var from = await _repository.FindUomAsync(request.FromUomId, cancellationToken).ConfigureAwait(false);
        var to = await _repository.FindUomAsync(request.ToUomId, cancellationToken).ConfigureAwait(false);

        if (from is null || to is null)
        {
            return CatalogErrors.UomNotFound();
        }

        var existing = await _repository
            .FindUomConversionAsync(request.FromUomId, request.ToUomId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.ChangeFactor(request.Factor);
            return new UomConversionResponse(from.Id, from.Code, to.Id, to.Code, existing.Factor);
        }

        // The reverse direction is derivable, so storing it as well would create two rows that can
        // disagree — and a stock figure that depends on which one a query happened to read.
        var reverse = await _repository
            .FindUomConversionAsync(request.ToUomId, request.FromUomId, cancellationToken)
            .ConfigureAwait(false);

        if (reverse is not null)
        {
            return CatalogErrors.ConversionExists();
        }

        UomConversion conversion;

        try
        {
            conversion = UomConversion.Create(_guids.NewId(), _tenant.OrganizationId, from, to, request.Factor);
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        _repository.AddUomConversion(conversion);

        return new UomConversionResponse(from.Id, from.Code, to.Id, to.Code, conversion.Factor);
    }
}

/// <summary>Lists the tenant's unit conversions.</summary>
public sealed class ListUomConversionsHandler
    : IUseCase<ListUomConversionsRequest, IReadOnlyList<UomConversionResponse>>
{
    private readonly ICatalogRepository _repository;

    public ListUomConversionsHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<UomConversionResponse>>> HandleAsync(
        ListUomConversionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversions = await _repository.GetUomConversionsAsync(cancellationToken).ConfigureAwait(false);
        var uoms = (await _repository.GetUomsAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(u => u.Id, u => u.Code);

        return conversions
            .Select(c => new UomConversionResponse(
                c.FromUomId,
                uoms.TryGetValue(c.FromUomId, out var fromCode) ? fromCode : string.Empty,
                c.ToUomId,
                uoms.TryGetValue(c.ToUomId, out var toCode) ? toCode : string.Empty,
                c.Factor))
            .OrderBy(c => c.FromCode, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>Mapping between catalog entities and their contracts. Entities are never returned.</summary>
internal static class CatalogMapping
{
    public static CategoryResponse ToResponse(Category category) =>
        new(
            category.Id,
            category.Code,
            category.Name,
            category.NameBn,
            category.ParentId,
            category.Depth,
            category.SortOrder,
            category.Status);

    public static BrandResponse ToResponse(Brand brand) =>
        new(brand.Id, brand.Code, brand.Name, brand.NameBn, brand.Status);

    public static UomResponse ToResponse(UnitOfMeasure uom) =>
        new(uom.Id, uom.Code, uom.Name, uom.NameBn, uom.Kind, uom.DecimalPlaces, uom.Status);

    public static ProductResponse ToResponse(Product product) =>
        new(
            product.Id,
            product.Code,
            product.Name,
            product.NameBn,
            product.Description,
            product.CategoryId,
            product.BrandId,
            product.TaxClassId,
            product.BaseUomId,
            product.ProductType,
            product.TrackInventory,
            product.TrackBatch,
            product.TrackSerial,
            product.TrackExpiry,
            product.NearExpiryDays,
            product.Status,
            product.Variants
                .OrderByDescending(v => v.IsDefault)
                .ThenBy(v => v.Sku, StringComparer.Ordinal)
                .Select(ToResponse)
                .ToArray(),
            product.Attributes
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new ProductAttributeResponse(a.Name, a.Value, a.SortOrder))
                .ToArray());

    public static VariantResponse ToResponse(ProductVariant variant) =>
        new(
            variant.Id,
            variant.Sku,
            variant.Name,
            variant.BasePrice,
            variant.LastPurchaseCost,
            variant.UomId,
            variant.PackSize,
            variant.WeightInGrams,
            variant.IsDefault,
            variant.Status,
            variant.Barcodes
                .OrderByDescending(b => b.IsPrimary)
                .ThenBy(b => b.Code, StringComparer.Ordinal)
                .Select(b => new BarcodeResponse(b.Id, b.Code, b.Type, b.PackQuantity, b.IsPrimary))
                .ToArray());

    public static BranchStockPolicyResponse ToResponse(ProductBranchSetting setting) =>
        new(
            setting.ProductVariantId,
            setting.BranchId,
            setting.MinStock,
            setting.ReorderLevel,
            setting.ReorderQuantity,
            setting.IsSellable);
}

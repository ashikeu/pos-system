using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Catalog.UseCases;

/// <summary>Searches products for the admin list screen (offset paging, ADR-0014).</summary>
public sealed class SearchProductsHandler : IUseCase<SearchProductsRequest, PagedResult<ProductListItem>>
{
    private readonly ICatalogRepository _repository;

    public SearchProductsHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<PagedResult<ProductListItem>>> HandleAsync(
        SearchProductsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var criteria = new ProductSearchCriteria(
            request.Page ?? new PageRequest(),
            request.Search,
            request.CategoryId,
            request.BrandId,
            request.Status,
            request.IncludeArchived);

        return await _repository.SearchProductsAsync(criteria, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Returns one product with its variants, barcodes and attributes.</summary>
public sealed class GetProductHandler : IUseCase<GetProductRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;

    public GetProductHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        GetProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository.FindProductAsync(request.ProductId, cancellationToken).ConfigureAwait(false);

        return product is null
            ? CatalogErrors.ProductNotFound()
            : CatalogMapping.ToResponse(product);
    }
}

/// <summary>
/// Creates a product with its first variant, and optionally a barcode (FR-CAT-01). Subject to the
/// plan's product limit (FR-TEN-05), which is checked inside this transaction so the count it reads
/// is the count it adds to.
/// </summary>
public sealed class CreateProductHandler : IUseCase<CreateProductRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IPlanLimitGuard _limits;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public CreateProductHandler(
        ICatalogRepository repository,
        IPlanLimitGuard limits,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _limits = limits;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capacity = await _limits
            .EnsureCapacityAsync(PlanFeatureCode.Products, cancellationToken)
            .ConfigureAwait(false);

        if (capacity.IsFailure)
        {
            return capacity.Error;
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        var sku = string.IsNullOrWhiteSpace(request.Sku) ? code : request.Sku.Trim().ToUpperInvariant();

        if (await _repository.ProductCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.CodeTaken("product", code);
        }

        if (await _repository.SkuExistsAsync(sku, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.SkuTaken(sku);
        }

        var uom = await _repository.FindUomAsync(request.BaseUomId, cancellationToken).ConfigureAwait(false);

        if (uom is null)
        {
            return CatalogErrors.UomNotFound();
        }

        var referenceCheck = await CheckReferencesAsync(request.CategoryId, request.BrandId, cancellationToken)
            .ConfigureAwait(false);

        if (referenceCheck is not null)
        {
            return referenceCheck;
        }

        Product product;

        try
        {
            product = Product.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                code,
                request.Name,
                request.BaseUomId,
                request.ProductType);

            product.UpdateDetails(request.Name, request.NameBn, request.Description);
            product.Classify(request.CategoryId, request.BrandId, request.TaxClassId);

            if (request.ProductType == ProductType.Standard)
            {
                product.SetTrackingPolicy(
                    trackInventory: true,
                    request.TrackBatch,
                    request.TrackSerial,
                    request.TrackExpiry,
                    request.NearExpiryDays);
            }
            else
            {
                product.SetTrackingPolicy(false, false, false, false);
            }

            var variant = product.AddVariant(
                _guids.NewId(),
                sku,
                name: null,
                request.BasePrice,
                request.BaseUomId);

            if (!string.IsNullOrWhiteSpace(request.Barcode))
            {
                var barcode = Barcode.Normalize(request.Barcode);

                if (await _repository.BarcodeExistsAsync(barcode, null, cancellationToken).ConfigureAwait(false))
                {
                    return CatalogErrors.BarcodeTaken(barcode);
                }

                variant.AddBarcode(_guids.NewId(), barcode, request.BarcodeType);
            }

            if (request.Activate)
            {
                product.Activate();
            }
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        _repository.AddProduct(product);

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.product_created",
                nameof(Product),
                product.Id,
                After: new { product.Code, product.Name, Sku = sku, request.BasePrice, product.Status }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(product);
    }

    private async Task<Error?> CheckReferencesAsync(
        Guid? categoryId,
        Guid? brandId,
        CancellationToken cancellationToken)
    {
        if (categoryId is { } category
            && await _repository.FindCategoryAsync(category, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.CategoryNotFound();
        }

        if (brandId is { } brand
            && await _repository.FindBrandAsync(brand, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.BrandNotFound();
        }

        return null;
    }
}

/// <summary>Changes a product's details, classification and tracking policy.</summary>
public sealed class UpdateProductHandler : IUseCase<UpdateProductRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public UpdateProductHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        UpdateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository.FindProductAsync(request.ProductId, cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound();
        }

        if (request.CategoryId is { } categoryId
            && await _repository.FindCategoryAsync(categoryId, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.CategoryNotFound();
        }

        if (request.BrandId is { } brandId
            && await _repository.FindBrandAsync(brandId, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.BrandNotFound();
        }

        var before = CatalogMapping.ToResponse(product);

        try
        {
            product.UpdateDetails(request.Name, request.NameBn, request.Description);
            product.Classify(request.CategoryId, request.BrandId, request.TaxClassId);

            if (product.ProductType == ProductType.Standard)
            {
                product.SetTrackingPolicy(
                    trackInventory: true,
                    request.TrackBatch,
                    request.TrackSerial,
                    request.TrackExpiry,
                    request.NearExpiryDays);
            }
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        var after = CatalogMapping.ToResponse(product);

        await _audit.WriteAsync(
            new AuditEntry("catalog.product_updated", nameof(Product), product.Id, before, after),
            cancellationToken).ConfigureAwait(false);

        return after;
    }
}

/// <summary>
/// Moves a product through its lifecycle. Archiving is the catalog's delete: history, stock
/// movements and tax filings all reference the product, so it is withdrawn rather than removed
/// (NFR-09).
/// </summary>
public sealed class SetProductStatusHandler : IUseCase<SetProductStatusRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public SetProductStatusHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        SetProductStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository.FindProductAsync(request.ProductId, cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound();
        }

        try
        {
            switch (request.Status)
            {
                case CatalogStatus.Active:
                    product.Activate();
                    break;
                case CatalogStatus.Inactive:
                    product.Deactivate();
                    break;
                case CatalogStatus.Archived:
                    product.Archive();
                    break;
                case CatalogStatus.Draft:
                default:
                    return CatalogErrors.Invalid("A product cannot be moved back to draft.");
            }
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.product_status_changed",
                nameof(Product),
                product.Id,
                After: new { product.Code, product.Status }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(product);
    }
}

/// <summary>Adds a variant to an existing product.</summary>
public sealed class AddVariantHandler : IUseCase<AddVariantRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public AddVariantHandler(ICatalogRepository repository, IAuditWriter audit, IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        AddVariantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository.FindProductAsync(request.ProductId, cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound();
        }

        var sku = (request.Sku ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.SkuExistsAsync(sku, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.SkuTaken(sku);
        }

        var uomId = request.UomId ?? product.BaseUomId;

        if (await _repository.FindUomAsync(uomId, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.UomNotFound();
        }

        try
        {
            var variant = product.AddVariant(
                _guids.NewId(),
                sku,
                request.Name,
                request.BasePrice,
                uomId,
                request.PackSize);

            variant.UpdateDetails(request.Name, uomId, request.PackSize, request.WeightInGrams);
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.variant_added",
                nameof(ProductVariant),
                product.Id,
                After: new { product.Code, Sku = sku, request.BasePrice }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(product);
    }
}

/// <summary>Changes a variant's price, unit, pack size or default flag.</summary>
public sealed class UpdateVariantHandler : IUseCase<UpdateVariantRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public UpdateVariantHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        UpdateVariantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository
            .FindProductByVariantAsync(request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.VariantNotFound();
        }

        var variant = product.Variants.First(v => v.Id == request.VariantId);
        var before = CatalogMapping.ToResponse(variant);

        var uomId = request.UomId ?? variant.UomId;

        if (uomId != variant.UomId
            && await _repository.FindUomAsync(uomId, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.UomNotFound();
        }

        try
        {
            variant.UpdateDetails(request.Name, uomId, request.PackSize, request.WeightInGrams);
            variant.ChangeBasePrice(request.BasePrice);

            if (request.MakeDefault)
            {
                product.SetDefaultVariant(variant.Id);
            }
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.variant_updated",
                nameof(ProductVariant),
                variant.Id,
                before,
                CatalogMapping.ToResponse(variant)),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(product);
    }
}

/// <summary>Moves a variant through its lifecycle.</summary>
public sealed class SetVariantStatusHandler : IUseCase<SetVariantStatusRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;

    public SetVariantStatusHandler(ICatalogRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        SetVariantStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository
            .FindProductByVariantAsync(request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.VariantNotFound();
        }

        var variant = product.Variants.First(v => v.Id == request.VariantId);

        try
        {
            switch (request.Status)
            {
                case CatalogStatus.Active:
                    variant.Activate();
                    break;
                case CatalogStatus.Inactive:
                    variant.Deactivate();
                    break;
                case CatalogStatus.Archived:
                case CatalogStatus.Draft:
                default:
                    return CatalogErrors.Invalid(
                        "Archive the product to archive its variants; a variant cannot be archived alone.");
            }
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.variant_status_changed",
                nameof(ProductVariant),
                variant.Id,
                After: new { variant.Sku, variant.Status }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(product);
    }
}

/// <summary>
/// Adds a barcode to a variant. Uniqueness is per tenant across every variant: a code that
/// identified two products would make a scan ambiguous, which at a till means the wrong price
/// (FR-CAT-03).
/// </summary>
public sealed class AddBarcodeHandler : IUseCase<AddBarcodeRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public AddBarcodeHandler(ICatalogRepository repository, IAuditWriter audit, IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        AddBarcodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository
            .FindProductByVariantAsync(request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.VariantNotFound();
        }

        string code;

        try
        {
            code = Barcode.Normalize(request.Code);
        }
        catch (ArgumentException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        if (await _repository.BarcodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogErrors.BarcodeTaken(code);
        }

        var variant = product.Variants.First(v => v.Id == request.VariantId);

        try
        {
            var barcode = variant.AddBarcode(_guids.NewId(), code, request.Type, request.PackQuantity);

            if (request.MakePrimary)
            {
                variant.MakeBarcodePrimary(barcode.Id);
            }
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.barcode_added",
                nameof(Barcode),
                variant.Id,
                After: new { variant.Sku, Barcode = code, request.Type, request.PackQuantity }),
            cancellationToken).ConfigureAwait(false);

        return CatalogMapping.ToResponse(product);
    }
}

/// <summary>Sets a named characteristic on a product.</summary>
public sealed class SetProductAttributeHandler : IUseCase<SetProductAttributeRequest, ProductResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly IGuidGenerator _guids;

    public SetProductAttributeHandler(ICatalogRepository repository, IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _guids = guids;
    }

    public async Task<Result<ProductResponse>> HandleAsync(
        SetProductAttributeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _repository.FindProductAsync(request.ProductId, cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound();
        }

        try
        {
            product.SetAttribute(_guids.NewId(), request.Name, request.Value, request.SortOrder);
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        return CatalogMapping.ToResponse(product);
    }
}

/// <summary>Sets a variant's stock policy at one branch (FR-CAT-08).</summary>
public sealed class SetBranchStockPolicyHandler
    : IUseCase<SetBranchStockPolicyRequest, BranchStockPolicyResponse>
{
    private readonly ICatalogRepository _repository;
    private readonly ITenancyRepository _tenancy;
    private readonly ITenantContext _tenant;
    private readonly IGuidGenerator _guids;

    public SetBranchStockPolicyHandler(
        ICatalogRepository repository,
        ITenancyRepository tenancy,
        ITenantContext tenant,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenancy = tenancy;
        _tenant = tenant;
        _guids = guids;
    }

    public async Task<Result<BranchStockPolicyResponse>> HandleAsync(
        SetBranchStockPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _repository.FindVariantAsync(request.VariantId, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.VariantNotFound();
        }

        // The branch is checked through the tenancy module's contract rather than by querying its
        // tables: modules meet at their contracts, not at each other's entities.
        if (await _tenancy.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("tenancy.branch_not_found", "That branch does not exist.");
        }

        var setting = await _repository
            .FindBranchSettingAsync(request.VariantId, request.BranchId, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            setting = ProductBranchSetting.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                request.VariantId,
                request.BranchId);

            _repository.AddBranchSetting(setting);
        }

        try
        {
            setting.SetThresholds(request.MinStock, request.ReorderLevel, request.ReorderQuantity);
            setting.SetSellable(request.IsSellable);
        }
        catch (DomainException ex)
        {
            return CatalogErrors.Invalid(ex.Message);
        }

        return CatalogMapping.ToResponse(setting);
    }
}

/// <summary>Lists a variant's per-branch stock policies.</summary>
public sealed class ListBranchStockPoliciesHandler
    : IUseCase<ListBranchStockPoliciesRequest, IReadOnlyList<BranchStockPolicyResponse>>
{
    private readonly ICatalogRepository _repository;

    public ListBranchStockPoliciesHandler(ICatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<BranchStockPolicyResponse>>> HandleAsync(
        ListBranchStockPoliciesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _repository
            .GetBranchSettingsAsync(request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        return settings.Select(CatalogMapping.ToResponse).ToArray();
    }
}

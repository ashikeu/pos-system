using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Domain.Catalog;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Catalog.Persistence;

/// <summary>
/// Catalog data access. Writes load aggregates with their children; reads for screens are
/// projections, because a list of fifty products must not drag fifty variant collections with it.
/// </summary>
internal sealed class CatalogRepository : ICatalogRepository, ICatalogStatistics
{
    private readonly RetailFlowDbContext _context;

    public CatalogRepository(RetailFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    // ------------------------------------------------------------------ categories

    public Task<Category?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _context.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Categories.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(c => c.Status == CatalogStatus.Active);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CategoryCodeExistsAsync(
        string code,
        Guid? exceptCategoryId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.Categories
            .AnyAsync(
                c => c.Code == normalized && (exceptCategoryId == null || c.Id != exceptCategoryId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Walks down from the category counting levels. Done in memory over the tenant's categories
    /// because the tree is bounded to four levels and a few hundred rows; a recursive CTE would be
    /// more code and a provider-specific query for no measurable gain (ADR-0003).
    /// </summary>
    public async Task<int> GetSubtreeHeightAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var all = await _context.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.ParentId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byParent = all
            .Where(c => c.ParentId is not null)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());

        var height = 0;
        var level = new[] { categoryId };

        while (true)
        {
            var next = level
                .SelectMany(id => byParent.TryGetValue(id, out var children) ? children : [])
                .ToArray();

            if (next.Length == 0)
            {
                return height;
            }

            height++;
            level = next;

            if (height > Category.MaxDepth)
            {
                // Defensive: a cycle cannot be created through the aggregate, but a hand-edited row
                // must not turn into an infinite loop inside a request.
                return height;
            }
        }
    }

    public Task<bool> CategoryHasChildrenAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _context.Categories.AnyAsync(c => c.ParentId == categoryId, cancellationToken);

    public Task<bool> CategoryHasProductsAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        _context.Products.AnyAsync(p => p.CategoryId == categoryId, cancellationToken);

    public void AddCategory(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        _context.Categories.Add(category);
    }

    // ------------------------------------------------------------------ brands

    public Task<Brand?> FindBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
        _context.Brands.FirstOrDefaultAsync(b => b.Id == brandId, cancellationToken);

    public async Task<IReadOnlyList<Brand>> GetBrandsAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Brands.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(b => b.Status == CatalogStatus.Active);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> BrandCodeExistsAsync(
        string code,
        Guid? exceptBrandId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.Brands
            .AnyAsync(b => b.Code == normalized && (exceptBrandId == null || b.Id != exceptBrandId), cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddBrand(Brand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        _context.Brands.Add(brand);
    }

    // ------------------------------------------------------------------ units

    public Task<UnitOfMeasure?> FindUomAsync(Guid uomId, CancellationToken cancellationToken = default) =>
        _context.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Id == uomId, cancellationToken);

    public async Task<IReadOnlyList<UnitOfMeasure>> GetUomsAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.UnitsOfMeasure.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(u => u.Status == CatalogStatus.Active);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UomCodeExistsAsync(
        string code,
        Guid? exceptUomId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.UnitsOfMeasure
            .AnyAsync(u => u.Code == normalized && (exceptUomId == null || u.Id != exceptUomId), cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddUom(UnitOfMeasure uom)
    {
        ArgumentNullException.ThrowIfNull(uom);
        _context.UnitsOfMeasure.Add(uom);
    }

    public async Task<IReadOnlyList<UomConversion>> GetUomConversionsAsync(
        CancellationToken cancellationToken = default) =>
        await _context.UomConversions.ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<UomConversion?> FindUomConversionAsync(
        Guid fromUomId,
        Guid toUomId,
        CancellationToken cancellationToken = default) =>
        _context.UomConversions.FirstOrDefaultAsync(
            c => c.FromUomId == fromUomId && c.ToUomId == toUomId,
            cancellationToken);

    public void AddUomConversion(UomConversion conversion)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        _context.UomConversions.Add(conversion);
    }

    // ------------------------------------------------------------------ products

    public Task<Product?> FindProductAsync(Guid productId, CancellationToken cancellationToken = default) =>
        ProductAggregate().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public async Task<Product?> FindProductByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await ProductAggregate()
            .FirstOrDefaultAsync(p => p.Code == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ProductCodeExistsAsync(
        string code,
        Guid? exceptProductId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.Products
            .AnyAsync(
                p => p.Code == normalized && (exceptProductId == null || p.Id != exceptProductId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SkuExistsAsync(
        string sku,
        Guid? exceptVariantId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(sku);

        return await _context.ProductVariants
            .AnyAsync(v => v.Sku == normalized && (exceptVariantId == null || v.Id != exceptVariantId), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> BarcodeExistsAsync(
        string code,
        Guid? exceptBarcodeId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.Barcodes
            .AnyAsync(b => b.Code == normalized && (exceptBarcodeId == null || b.Id != exceptBarcodeId), cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddProduct(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        _context.Products.Add(product);
    }

    public Task<ProductVariant?> FindVariantAsync(Guid variantId, CancellationToken cancellationToken = default) =>
        _context.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId, cancellationToken);

    public async Task<Product?> FindProductByVariantAsync(
        Guid variantId,
        CancellationToken cancellationToken = default)
    {
        var productId = await _context.ProductVariants
            .Where(v => v.Id == variantId)
            .Select(v => (Guid?)v.ProductId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return productId is { } id
            ? await FindProductAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<PagedResult<ProductListItem>> SearchProductsAsync(
        ProductSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = _context.Products.AsNoTracking();

        if (!criteria.IncludeArchived)
        {
            query = query.Where(p => p.Status != CatalogStatus.Archived);
        }

        if (criteria.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        if (criteria.CategoryId is { } categoryId)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (criteria.BrandId is { } brandId)
        {
            query = query.Where(p => p.BrandId == brandId);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var term = criteria.Search.Trim();
            var upper = term.ToUpperInvariant();

            // Code, SKU and barcode are matched exactly (they are scanned or pasted); the name is
            // matched as a prefix, which an index can serve — a leading wildcard could not.
            query = query.Where(p =>
                p.Code == upper
                || p.Name.StartsWith(term)
                || (p.NameBn != null && p.NameBn.StartsWith(term))
                || _context.ProductVariants.Any(v => v.ProductId == p.Id && v.Sku == upper)
                || _context.Barcodes.Any(b =>
                    b.Code == upper
                    && _context.ProductVariants.Any(v => v.Id == b.ProductVariantId && v.ProductId == p.Id)));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var page = criteria.Page;

        var sorted = (page.SortBy?.ToLowerInvariant()) switch
        {
            // An explicit allow-list, never a free-form field name (ADR-0014).
            "code" => page.Descending ? query.OrderByDescending(p => p.Code) : query.OrderBy(p => p.Code),
            "status" => page.Descending ? query.OrderByDescending(p => p.Status) : query.OrderBy(p => p.Status),
            "created" => page.Descending
                ? query.OrderByDescending(p => p.CreatedAtUtc)
                : query.OrderBy(p => p.CreatedAtUtc),
            _ => page.Descending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
        };

        var items = await sorted
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .Select(p => new ProductListItem(
                p.Id,
                p.Code,
                p.Name,
                p.NameBn,
                _context.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                _context.Brands.Where(b => b.Id == p.BrandId).Select(b => b.Name).FirstOrDefault(),
                p.Status,
                p.ProductType,
                _context.ProductVariants.Count(v => v.ProductId == p.Id),
                _context.ProductVariants
                    .Where(v => v.ProductId == p.Id && v.IsDefault)
                    .Select(v => v.Sku)
                    .FirstOrDefault(),
                _context.ProductVariants
                    .Where(v => v.ProductId == p.Id && v.IsDefault)
                    .Select(v => (decimal?)v.BasePrice)
                    .FirstOrDefault(),
                p.TrackInventory))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<ProductListItem>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<VariantLookup?> LookupAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        // Barcode first, because that is what a scanner sends; then SKU, then the product code,
        // which is what someone typing from a label or a phone call uses.
        var byBarcode = await LookupProjection(
                _context.ProductVariants.AsNoTracking()
                    .Where(v => _context.Barcodes.Any(
                        b => b.ProductVariantId == v.Id && b.Code == normalized)),
                normalized)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (byBarcode is not null)
        {
            return byBarcode;
        }

        // SKU is unique per tenant (UX_ProductVariant_Org_Sku), so this matches at most one row.
        var bySku = await LookupProjection(
                _context.ProductVariants.AsNoTracking().Where(v => v.Sku == normalized),
                matchedBarcode: null)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bySku is not null)
        {
            return bySku;
        }

        return await LookupProjection(
                _context.ProductVariants.AsNoTracking()
                    .Where(v => v.IsDefault
                        && _context.Products.Any(p => p.Id == v.ProductId && p.Code == normalized)),
                matchedBarcode: null)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<VariantLookup?> LookupVariantAsync(
        Guid variantId,
        CancellationToken cancellationToken = default) =>
        LookupProjection(
                _context.ProductVariants.AsNoTracking().Where(v => v.Id == variantId),
                matchedBarcode: null)
            .FirstOrDefaultAsync(cancellationToken);

    // ------------------------------------------------------------------ branch settings

    public Task<ProductBranchSetting?> FindBranchSettingAsync(
        Guid productVariantId,
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        _context.ProductBranchSettings.FirstOrDefaultAsync(
            s => s.ProductVariantId == productVariantId && s.BranchId == branchId,
            cancellationToken);

    public async Task<IReadOnlyList<ProductBranchSetting>> GetBranchSettingsAsync(
        Guid productVariantId,
        CancellationToken cancellationToken = default) =>
        await _context.ProductBranchSettings
            .Where(s => s.ProductVariantId == productVariantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void AddBranchSetting(ProductBranchSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _context.ProductBranchSettings.Add(setting);
    }

    // ------------------------------------------------------------------ statistics

    public Task<int> CountProductsAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        _context.Products
            .IgnoreQueryFilters()
            .CountAsync(
                p => p.OrganizationId == organizationId && p.Status != CatalogStatus.Archived,
                cancellationToken);

    // ------------------------------------------------------------------ helpers

    private IQueryable<Product> ProductAggregate() =>
        _context.Products
            .Include(p => p.Variants)
                .ThenInclude(v => v.Barcodes)
            .Include(p => p.Attributes);

    /// <summary>
    /// Projects the view of a variant that the till needs. The caller narrows the variants
    /// <em>before</em> the projection, because EF cannot translate a predicate written against the
    /// members of a projected type, and a filter applied afterwards would be evaluated in memory.
    /// </summary>
    /// <param name="matchedBarcode">
    /// The scanned code, or null when the variant was found by SKU, product code or id. It decides
    /// the pack quantity: a case barcode sells twelve where the same variant scanned as a single
    /// unit sells one, and a lookup that scanned nothing sells one.
    /// </param>
    private IQueryable<VariantLookup> LookupProjection(
        IQueryable<ProductVariant> variants,
        string? matchedBarcode) =>
        from variant in variants
        join product in _context.Products.AsNoTracking() on variant.ProductId equals product.Id
        join uom in _context.UnitsOfMeasure.AsNoTracking() on variant.UomId equals uom.Id
        select new VariantLookup(
            product.Id,
            product.Name,
            product.NameBn,
            variant.Id,
            variant.Sku,
            variant.Name,
            variant.BasePrice,
            product.TaxClassId,
            uom.Id,
            uom.Code,
            uom.DecimalPlaces,
            matchedBarcode == null
                ? 1m
                : _context.Barcodes
                    .Where(b => b.ProductVariantId == variant.Id && b.Code == matchedBarcode)
                    .Select(b => (decimal?)b.PackQuantity)
                    .FirstOrDefault() ?? 1m,
            matchedBarcode,
            product.TrackInventory,
            product.TrackBatch,
            product.TrackSerial,
            product.TrackExpiry,

            // Both have to be active: an active product with a withdrawn variant is still not
            // sellable as that variant, and the till needs to say so rather than price it.
            product.Status == CatalogStatus.Active && variant.Status == CatalogStatus.Active);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Domain.Catalog;

namespace RetailFlow.Application.Catalog.Abstractions;

/// <summary>
/// A row on the product list screen. A projection rather than the aggregate: the list shows a dozen
/// fields and a product with fifty variants would otherwise be loaded in full to render one line.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="Code">Its code.</param>
/// <param name="Name">Its name.</param>
/// <param name="NameBn">Its Bangla name.</param>
/// <param name="CategoryName">Category, when it has one.</param>
/// <param name="BrandName">Brand, when it has one.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="ProductType">Standard or service.</param>
/// <param name="VariantCount">How many variants it has.</param>
/// <param name="DefaultSku">SKU of its default variant.</param>
/// <param name="BasePrice">Base price of its default variant.</param>
/// <param name="TrackInventory">Whether it moves the stock ledger.</param>
public sealed record ProductListItem(
    Guid ProductId,
    string Code,
    string Name,
    string? NameBn,
    string? CategoryName,
    string? BrandName,
    CatalogStatus Status,
    ProductType ProductType,
    int VariantCount,
    string? DefaultSku,
    decimal? BasePrice,
    bool TrackInventory);

/// <summary>What a product search is filtering by. Every filter is optional.</summary>
/// <param name="Page">Which page to return.</param>
/// <param name="Search">Matches code, name, SKU or barcode.</param>
/// <param name="CategoryId">Restricts to a category.</param>
/// <param name="BrandId">Restricts to a brand.</param>
/// <param name="Status">Restricts to a lifecycle state.</param>
/// <param name="IncludeArchived">
/// Whether archived products are included. Off by default, because an archived product in a POS
/// search result is a product a cashier will try to sell.
/// </param>
public sealed record ProductSearchCriteria(
    PageRequest Page,
    string? Search = null,
    Guid? CategoryId = null,
    Guid? BrandId = null,
    CatalogStatus? Status = null,
    bool IncludeArchived = false);

/// <summary>
/// What a barcode scan resolves to: the variant, its product, the tax class to price it with, and
/// how many units this particular code sells.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="ProductName">Name to show on screen.</param>
/// <param name="ProductNameBn">Bangla name, for a Bangla till.</param>
/// <param name="VariantId">The variant that was scanned.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="VariantName">Variant label, when it has one.</param>
/// <param name="BasePrice">The variant's base price, the floor of price resolution.</param>
/// <param name="TaxClassId">Tax class, from the product or the tenant default.</param>
/// <param name="UomId">Unit the variant sells in.</param>
/// <param name="UomCode">That unit's code, for the screen.</param>
/// <param name="UomDecimalPlaces">Whether a fractional quantity is legal.</param>
/// <param name="PackQuantity">Units this code sells in one scan — 24 for a case barcode.</param>
/// <param name="MatchedBarcode">The code that matched, or null when the lookup was by SKU or id.</param>
/// <param name="TrackInventory">Whether selling it moves the ledger.</param>
/// <param name="TrackBatch">Whether a batch must be chosen.</param>
/// <param name="TrackSerial">Whether a serial must be chosen.</param>
/// <param name="TrackExpiry">Whether expiry must be checked.</param>
/// <param name="IsSellable">
/// False when the product or variant is not active. The lookup still returns the row, so the till
/// can say "this product is withdrawn" instead of "unknown barcode".
/// </param>
public sealed record VariantLookup(
    Guid ProductId,
    string ProductName,
    string? ProductNameBn,
    Guid VariantId,
    string Sku,
    string? VariantName,
    decimal BasePrice,
    Guid? TaxClassId,
    Guid UomId,
    string UomCode,
    int UomDecimalPlaces,
    decimal PackQuantity,
    string? MatchedBarcode,
    bool TrackInventory,
    bool TrackBatch,
    bool TrackSerial,
    bool TrackExpiry,
    bool IsSellable);

/// <summary>
/// Data access for the catalog module. Writes go through the aggregates; the reads here are either
/// aggregate loads or deliberate projections for a screen.
/// </summary>
public interface ICatalogRepository
{
    // --- categories ---

    Task<Category?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Category>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> CategoryCodeExistsAsync(
        string code,
        Guid? exceptCategoryId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many levels sit below a category. Needed before moving it, because the depth limit
    /// applies to the whole subtree and the aggregate cannot see its children.
    /// </summary>
    Task<int> GetSubtreeHeightAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<bool> CategoryHasChildrenAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<bool> CategoryHasProductsAsync(Guid categoryId, CancellationToken cancellationToken = default);

    void AddCategory(Category category);

    // --- brands ---

    Task<Brand?> FindBrandAsync(Guid brandId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Brand>> GetBrandsAsync(bool includeInactive, CancellationToken cancellationToken = default);

    Task<bool> BrandCodeExistsAsync(
        string code,
        Guid? exceptBrandId = null,
        CancellationToken cancellationToken = default);

    void AddBrand(Brand brand);

    // --- units ---

    Task<UnitOfMeasure?> FindUomAsync(Guid uomId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UnitOfMeasure>> GetUomsAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> UomCodeExistsAsync(
        string code,
        Guid? exceptUomId = null,
        CancellationToken cancellationToken = default);

    void AddUom(UnitOfMeasure uom);

    Task<IReadOnlyList<UomConversion>> GetUomConversionsAsync(CancellationToken cancellationToken = default);

    Task<UomConversion?> FindUomConversionAsync(
        Guid fromUomId,
        Guid toUomId,
        CancellationToken cancellationToken = default);

    void AddUomConversion(UomConversion conversion);

    // --- products ---

    /// <summary>Loads a product with its variants, barcodes and attributes, for a write.</summary>
    Task<Product?> FindProductAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<Product?> FindProductByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<bool> ProductCodeExistsAsync(
        string code,
        Guid? exceptProductId = null,
        CancellationToken cancellationToken = default);

    /// <summary>SKUs are unique per tenant across every product, so this is not scoped to one.</summary>
    Task<bool> SkuExistsAsync(
        string sku,
        Guid? exceptVariantId = null,
        CancellationToken cancellationToken = default);

    Task<bool> BarcodeExistsAsync(
        string code,
        Guid? exceptBarcodeId = null,
        CancellationToken cancellationToken = default);

    void AddProduct(Product product);

    Task<ProductVariant?> FindVariantAsync(Guid variantId, CancellationToken cancellationToken = default);

    /// <summary>The product that owns a variant, loaded as an aggregate for a write.</summary>
    Task<Product?> FindProductByVariantAsync(Guid variantId, CancellationToken cancellationToken = default);

    Task<PagedResult<ProductListItem>> SearchProductsAsync(
        ProductSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a scanned code to a variant: barcode first, then SKU, then product code. One query
    /// path, because a cashier does not know which of the three they just scanned.
    /// </summary>
    Task<VariantLookup?> LookupAsync(string code, CancellationToken cancellationToken = default);

    Task<VariantLookup?> LookupVariantAsync(Guid variantId, CancellationToken cancellationToken = default);

    // --- per-branch settings ---

    Task<ProductBranchSetting?> FindBranchSettingAsync(
        Guid productVariantId,
        Guid branchId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductBranchSetting>> GetBranchSettingsAsync(
        Guid productVariantId,
        CancellationToken cancellationToken = default);

    void AddBranchSetting(ProductBranchSetting setting);
}

/// <summary>
/// Catalog counts for other modules. Exposed as its own contract so the tenancy plan guard can
/// enforce the product limit without reaching into catalog entities (FR-TEN-05).
/// </summary>
public interface ICatalogStatistics
{
    /// <summary>
    /// Products that are not archived, for an explicit tenant. Archived products do not count
    /// against a plan: a shop that withdraws a line should not have to upgrade to add a new one.
    /// </summary>
    Task<int> CountProductsAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

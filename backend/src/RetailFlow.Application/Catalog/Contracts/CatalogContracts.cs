using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Catalog.Contracts;

// ----------------------------------------------------------------- categories

/// <param name="IncludeInactive">Whether withdrawn categories are listed too.</param>
public sealed record ListCategoriesRequest(bool IncludeInactive = false);

/// <param name="CategoryId">Identifier.</param>
/// <param name="Code">Short code.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="ParentId">Parent category, when nested.</param>
/// <param name="Depth">1 for a root category.</param>
/// <param name="SortOrder">Display order among siblings.</param>
/// <param name="Status">Lifecycle state.</param>
public sealed record CategoryResponse(
    Guid CategoryId,
    string Code,
    string Name,
    string? NameBn,
    Guid? ParentId,
    int Depth,
    int SortOrder,
    CatalogStatus Status);

/// <param name="Code">Short code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="ParentId">Parent category, for a nested one.</param>
/// <param name="SortOrder">Display order among siblings.</param>
public sealed record CreateCategoryRequest(
    string Code,
    string Name,
    string? NameBn = null,
    Guid? ParentId = null,
    int SortOrder = 0) : ITransactionalRequest;

/// <param name="CategoryId">Category to change.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="ParentId">New parent, or null to move it to the root.</param>
/// <param name="SortOrder">Display order among siblings.</param>
public sealed record UpdateCategoryRequest(
    Guid CategoryId,
    string Name,
    string? NameBn = null,
    Guid? ParentId = null,
    int SortOrder = 0) : ITransactionalRequest;

/// <param name="CategoryId">Category to withdraw.</param>
/// <param name="Status">Target state: Active, Inactive or Archived.</param>
public sealed record SetCategoryStatusRequest(Guid CategoryId, CatalogStatus Status) : ITransactionalRequest;

// ----------------------------------------------------------------- brands

/// <param name="IncludeInactive">Whether withdrawn brands are listed too.</param>
public sealed record ListBrandsRequest(bool IncludeInactive = false);

/// <param name="BrandId">Identifier.</param>
/// <param name="Code">Short code.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="Status">Lifecycle state.</param>
public sealed record BrandResponse(Guid BrandId, string Code, string Name, string? NameBn, CatalogStatus Status);

/// <param name="Code">Short code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
public sealed record CreateBrandRequest(string Code, string Name, string? NameBn = null) : ITransactionalRequest;

/// <param name="BrandId">Brand to change.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
public sealed record UpdateBrandRequest(Guid BrandId, string Name, string? NameBn = null) : ITransactionalRequest;

/// <param name="BrandId">Brand to withdraw.</param>
/// <param name="Status">Target state.</param>
public sealed record SetBrandStatusRequest(Guid BrandId, CatalogStatus Status) : ITransactionalRequest;

// ----------------------------------------------------------------- units of measure

/// <param name="IncludeInactive">Whether withdrawn units are listed too.</param>
public sealed record ListUomsRequest(bool IncludeInactive = false);

/// <param name="UomId">Identifier.</param>
/// <param name="Code">Short code, e.g. <c>KG</c>.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="Kind">What it measures.</param>
/// <param name="DecimalPlaces">Decimals a quantity may carry: 0 means whole units only.</param>
/// <param name="Status">Lifecycle state.</param>
public sealed record UomResponse(
    Guid UomId,
    string Code,
    string Name,
    string? NameBn,
    UomKind Kind,
    int DecimalPlaces,
    CatalogStatus Status);

/// <param name="Code">Short code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="Kind">What it measures; conversions stay inside one kind.</param>
/// <param name="DecimalPlaces">0 for countable things, up to 3 for weight and volume.</param>
public sealed record CreateUomRequest(
    string Code,
    string Name,
    string? NameBn = null,
    UomKind Kind = UomKind.Count,
    int DecimalPlaces = 0) : ITransactionalRequest;

/// <param name="FromUomId">The larger unit, e.g. a case.</param>
/// <param name="ToUomId">The smaller unit, e.g. a piece.</param>
/// <param name="Factor">How many <c>To</c> units are in one <c>From</c> unit.</param>
public sealed record CreateUomConversionRequest(Guid FromUomId, Guid ToUomId, decimal Factor) : ITransactionalRequest;

/// <param name="FromUomId">The larger unit.</param>
/// <param name="FromCode">Its code.</param>
/// <param name="ToUomId">The smaller unit.</param>
/// <param name="ToCode">Its code.</param>
/// <param name="Factor">The conversion factor.</param>
public sealed record UomConversionResponse(
    Guid FromUomId,
    string FromCode,
    Guid ToUomId,
    string ToCode,
    decimal Factor);

/// <summary>Lists the tenant's unit conversions.</summary>
public sealed record ListUomConversionsRequest;

// ----------------------------------------------------------------- products

/// <param name="Page">Which page of results.</param>
/// <param name="Search">Matches code, name, SKU or barcode.</param>
/// <param name="CategoryId">Restricts to a category.</param>
/// <param name="BrandId">Restricts to a brand.</param>
/// <param name="Status">Restricts to a lifecycle state.</param>
/// <param name="IncludeArchived">Whether archived products appear.</param>
public sealed record SearchProductsRequest(
    PageRequest Page,
    string? Search = null,
    Guid? CategoryId = null,
    Guid? BrandId = null,
    CatalogStatus? Status = null,
    bool IncludeArchived = false);

/// <param name="ProductId">The product to load.</param>
public sealed record GetProductRequest(Guid ProductId);

/// <param name="ProductId">Identifier.</param>
/// <param name="Code">Internal code.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="Description">Longer description.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="BrandId">Brand.</param>
/// <param name="TaxClassId">Tax class; null means the tenant default applies.</param>
/// <param name="BaseUomId">Unit the product's stock is kept in.</param>
/// <param name="ProductType">Standard or service.</param>
/// <param name="TrackInventory">Whether it moves the stock ledger.</param>
/// <param name="TrackBatch">Whether stock is identified by batch.</param>
/// <param name="TrackSerial">Whether each unit carries a serial.</param>
/// <param name="TrackExpiry">Whether stock carries an expiry date.</param>
/// <param name="NearExpiryDays">Days before expiry at which selling needs permission.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="Variants">Its variants.</param>
/// <param name="Attributes">Its named characteristics.</param>
public sealed record ProductResponse(
    Guid ProductId,
    string Code,
    string Name,
    string? NameBn,
    string? Description,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? TaxClassId,
    Guid BaseUomId,
    ProductType ProductType,
    bool TrackInventory,
    bool TrackBatch,
    bool TrackSerial,
    bool TrackExpiry,
    int? NearExpiryDays,
    CatalogStatus Status,
    IReadOnlyList<VariantResponse> Variants,
    IReadOnlyList<ProductAttributeResponse> Attributes);

/// <param name="VariantId">Identifier.</param>
/// <param name="Sku">Stock-keeping unit.</param>
/// <param name="Name">Variant label.</param>
/// <param name="BasePrice">Base price.</param>
/// <param name="LastPurchaseCost">Most recent purchase cost, when known.</param>
/// <param name="UomId">Unit it sells in.</param>
/// <param name="PackSize">Base units in one of this variant.</param>
/// <param name="WeightInGrams">Weight, for scales and delivery.</param>
/// <param name="IsDefault">Whether a bare product scan resolves to it.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="Barcodes">Its scannable codes.</param>
public sealed record VariantResponse(
    Guid VariantId,
    string Sku,
    string? Name,
    decimal BasePrice,
    decimal? LastPurchaseCost,
    Guid UomId,
    decimal PackSize,
    decimal? WeightInGrams,
    bool IsDefault,
    CatalogStatus Status,
    IReadOnlyList<BarcodeResponse> Barcodes);

/// <param name="BarcodeId">Identifier.</param>
/// <param name="Code">The code itself.</param>
/// <param name="Type">Symbology.</param>
/// <param name="PackQuantity">Units one scan sells.</param>
/// <param name="IsPrimary">Whether it is the variant's main code.</param>
public sealed record BarcodeResponse(
    Guid BarcodeId,
    string Code,
    BarcodeType Type,
    decimal PackQuantity,
    bool IsPrimary);

/// <param name="Name">Attribute name.</param>
/// <param name="Value">Attribute value.</param>
/// <param name="SortOrder">Display order.</param>
public sealed record ProductAttributeResponse(string Name, string Value, int SortOrder);

/// <summary>
/// Creates a product together with its first variant. One call rather than two, because a product
/// without a variant has no price and cannot be activated — so a two-step API would make the
/// invalid state reachable and then leave it lying around.
/// </summary>
/// <param name="Code">Internal code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="Sku">SKU of the first variant; defaults to the product code.</param>
/// <param name="BasePrice">Base price of the first variant.</param>
/// <param name="BaseUomId">Unit stock is kept in. Required.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="Description">Longer description.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="BrandId">Brand.</param>
/// <param name="TaxClassId">Tax class; the tenant default applies when omitted.</param>
/// <param name="ProductType">Standard or service.</param>
/// <param name="TrackBatch">Whether stock is identified by batch.</param>
/// <param name="TrackSerial">Whether each unit carries a serial.</param>
/// <param name="TrackExpiry">Whether stock carries an expiry date; needs batch tracking.</param>
/// <param name="NearExpiryDays">Days before expiry at which selling needs permission.</param>
/// <param name="Barcode">A barcode for the first variant.</param>
/// <param name="BarcodeType">Its symbology.</param>
/// <param name="Activate">Whether to activate the product immediately.</param>
public sealed record CreateProductRequest(
    string Code,
    string Name,
    decimal BasePrice,
    Guid BaseUomId,
    string? Sku = null,
    string? NameBn = null,
    string? Description = null,
    Guid? CategoryId = null,
    Guid? BrandId = null,
    Guid? TaxClassId = null,
    ProductType ProductType = ProductType.Standard,
    bool TrackBatch = false,
    bool TrackSerial = false,
    bool TrackExpiry = false,
    int? NearExpiryDays = null,
    string? Barcode = null,
    BarcodeType BarcodeType = BarcodeType.Ean13,
    bool Activate = true) : ITransactionalRequest;

/// <param name="ProductId">Product to change.</param>
/// <param name="Name">Name.</param>
/// <param name="NameBn">Bangla name.</param>
/// <param name="Description">Longer description.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="BrandId">Brand.</param>
/// <param name="TaxClassId">Tax class.</param>
/// <param name="TrackBatch">Whether stock is identified by batch.</param>
/// <param name="TrackSerial">Whether each unit carries a serial.</param>
/// <param name="TrackExpiry">Whether stock carries an expiry date.</param>
/// <param name="NearExpiryDays">Days before expiry at which selling needs permission.</param>
public sealed record UpdateProductRequest(
    Guid ProductId,
    string Name,
    string? NameBn = null,
    string? Description = null,
    Guid? CategoryId = null,
    Guid? BrandId = null,
    Guid? TaxClassId = null,
    bool TrackBatch = false,
    bool TrackSerial = false,
    bool TrackExpiry = false,
    int? NearExpiryDays = null) : ITransactionalRequest;

/// <param name="ProductId">Product to move.</param>
/// <param name="Status">Target state. Archived is permanent.</param>
public sealed record SetProductStatusRequest(Guid ProductId, CatalogStatus Status) : ITransactionalRequest;

/// <param name="ProductId">Product the variant belongs to.</param>
/// <param name="Sku">SKU, unique per tenant.</param>
/// <param name="BasePrice">Base price.</param>
/// <param name="Name">Variant label such as "500 ml".</param>
/// <param name="UomId">Unit it sells in; the product's base unit when omitted.</param>
/// <param name="PackSize">Base units in one of this variant.</param>
/// <param name="WeightInGrams">Weight, for scales and delivery.</param>
public sealed record AddVariantRequest(
    Guid ProductId,
    string Sku,
    decimal BasePrice,
    string? Name = null,
    Guid? UomId = null,
    decimal PackSize = 1m,
    decimal? WeightInGrams = null) : ITransactionalRequest;

/// <param name="VariantId">Variant to change.</param>
/// <param name="BasePrice">Base price.</param>
/// <param name="Name">Variant label.</param>
/// <param name="UomId">Unit it sells in.</param>
/// <param name="PackSize">Base units in one of this variant.</param>
/// <param name="WeightInGrams">Weight.</param>
/// <param name="MakeDefault">Whether it becomes the product's default variant.</param>
public sealed record UpdateVariantRequest(
    Guid VariantId,
    decimal BasePrice,
    string? Name = null,
    Guid? UomId = null,
    decimal PackSize = 1m,
    decimal? WeightInGrams = null,
    bool MakeDefault = false) : ITransactionalRequest;

/// <param name="VariantId">Variant to move.</param>
/// <param name="Status">Target state.</param>
public sealed record SetVariantStatusRequest(Guid VariantId, CatalogStatus Status) : ITransactionalRequest;

/// <param name="VariantId">Variant the code belongs to.</param>
/// <param name="Code">The barcode.</param>
/// <param name="Type">Symbology; the check digit is verified for the fixed-length ones.</param>
/// <param name="PackQuantity">Units one scan sells — 24 for a case barcode.</param>
/// <param name="MakePrimary">Whether it becomes the variant's main code.</param>
public sealed record AddBarcodeRequest(
    Guid VariantId,
    string Code,
    BarcodeType Type = BarcodeType.Ean13,
    decimal PackQuantity = 1m,
    bool MakePrimary = false) : ITransactionalRequest;

/// <param name="ProductId">Product to set an attribute on.</param>
/// <param name="Name">Attribute name.</param>
/// <param name="Value">Attribute value.</param>
/// <param name="SortOrder">Display order.</param>
public sealed record SetProductAttributeRequest(
    Guid ProductId,
    string Name,
    string Value,
    int SortOrder = 0) : ITransactionalRequest;

/// <param name="VariantId">Variant the settings belong to.</param>
/// <param name="BranchId">Branch they apply at.</param>
/// <param name="MinStock">Level below which the branch is short.</param>
/// <param name="ReorderLevel">Level at which to reorder; at or above the minimum.</param>
/// <param name="ReorderQuantity">How much to order.</param>
/// <param name="IsSellable">Whether this branch sells the variant at all.</param>
public sealed record SetBranchStockPolicyRequest(
    Guid VariantId,
    Guid BranchId,
    decimal? MinStock = null,
    decimal? ReorderLevel = null,
    decimal? ReorderQuantity = null,
    bool IsSellable = true) : ITransactionalRequest;

/// <param name="VariantId">The variant.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="MinStock">Minimum stock.</param>
/// <param name="ReorderLevel">Reorder level.</param>
/// <param name="ReorderQuantity">Reorder quantity.</param>
/// <param name="IsSellable">Whether the branch sells it.</param>
public sealed record BranchStockPolicyResponse(
    Guid VariantId,
    Guid BranchId,
    decimal? MinStock,
    decimal? ReorderLevel,
    decimal? ReorderQuantity,
    bool IsSellable);

/// <param name="VariantId">The variant whose per-branch policies to list.</param>
public sealed record ListBranchStockPoliciesRequest(Guid VariantId);

// ----------------------------------------------------------------- POS lookup

/// <summary>
/// What the till asks on every scan: resolve a code, and price it. One round trip, because a
/// cashier scanning a queue of items notices two (FR-POS-01).
/// </summary>
/// <param name="Code">The scanned barcode, SKU or product code.</param>
/// <param name="Quantity">Quantity being sold, which decides the qualifying price tier.</param>
/// <param name="BranchId">Branch to price for; the branch in scope when omitted.</param>
/// <param name="CustomerGroupId">Customer group, for its price list.</param>
public sealed record LookupProductRequest(
    string Code,
    decimal Quantity = 1m,
    Guid? BranchId = null,
    Guid? CustomerGroupId = null);

/// <param name="Product">What was found.</param>
/// <param name="Price">The resolved price and where it came from.</param>
/// <param name="Tax">The tax snapshot to charge with.</param>
/// <param name="Line">The line as it would be rung up at this quantity.</param>
public sealed record LookupProductResponse(
    VariantLookup Product,
    ResolvedPriceResponse Price,
    TaxSnapshotResponse Tax,
    LineTaxResponse Line);

/// <param name="UnitPrice">The price to charge.</param>
/// <param name="Source">Which source won: base price, price list, branch override, override.</param>
/// <param name="SourceId">That source's row, when it has one.</param>
/// <param name="MinQuantity">The tier that applied.</param>
public sealed record ResolvedPriceResponse(
    decimal UnitPrice,
    PriceSource Source,
    Guid? SourceId,
    decimal MinQuantity);

/// <param name="TaxClassId">The class.</param>
/// <param name="TaxRuleVersionId">The exact rate version used.</param>
/// <param name="Treatment">Exclusive, inclusive, exempt or zero-rated.</param>
/// <param name="VatRate">VAT rate as a fraction.</param>
/// <param name="SdRate">Supplementary duty rate as a fraction.</param>
/// <param name="SdInVatBase">Whether duty sits in the VAT base.</param>
/// <param name="ExemptionReason">Why tax is zero, when it is.</param>
public sealed record TaxSnapshotResponse(
    Guid? TaxClassId,
    Guid? TaxRuleVersionId,
    TaxTreatment Treatment,
    decimal VatRate,
    decimal SdRate,
    bool SdInVatBase,
    string? ExemptionReason);

/// <param name="Quantity">Quantity priced.</param>
/// <param name="UnitPrice">Unit price used.</param>
/// <param name="Gross">Quantity times price.</param>
/// <param name="NetOfDiscount">Gross less discounts.</param>
/// <param name="TaxableValue">Value VAT is charged on, duty included where it applies.</param>
/// <param name="SdAmount">Supplementary duty.</param>
/// <param name="VatAmount">VAT.</param>
/// <param name="LineTotal">What the customer pays.</param>
public sealed record LineTaxResponse(
    decimal Quantity,
    decimal UnitPrice,
    decimal Gross,
    decimal NetOfDiscount,
    decimal TaxableValue,
    decimal SdAmount,
    decimal VatAmount,
    decimal LineTotal);

// ----------------------------------------------------------------- import and export

/// <summary>
/// Imports products from a CSV file (FR-CAT-09). Always validated row by row first; with
/// <paramref name="DryRun"/> the report comes back and nothing is written, which is how a shop
/// checks a supplier's file before trusting it.
/// </summary>
/// <param name="Csv">The file contents. The first row is a header.</param>
/// <param name="DryRun">Validate and report without writing anything.</param>
/// <param name="UpdateExisting">
/// Whether a row whose code already exists updates that product. Off by default: an import that
/// silently rewrites prices is how a catalog loses its margins.
/// </param>
/// <param name="DefaultUomCode">Unit to use when a row does not name one.</param>
public sealed record ImportProductsRequest(
    string Csv,
    bool DryRun = true,
    bool UpdateExisting = false,
    string? DefaultUomCode = null) : ITransactionalRequest;

/// <param name="TotalRows">Rows found in the file, excluding the header.</param>
/// <param name="Created">Products created (zero on a dry run).</param>
/// <param name="Updated">Products updated (zero on a dry run).</param>
/// <param name="Failed">Rows that could not be imported.</param>
/// <param name="DryRun">Whether this was a validation-only pass.</param>
/// <param name="Errors">
/// One entry per rejected row, with the row number as it appears in the file so the person fixing
/// the spreadsheet can find it.
/// </param>
public sealed record ImportProductsResponse(
    int TotalRows,
    int Created,
    int Updated,
    int Failed,
    bool DryRun,
    IReadOnlyList<ImportRowError> Errors);

/// <param name="RowNumber">1-based row number in the file, header included.</param>
/// <param name="Code">The product code on that row, when it could be read.</param>
/// <param name="Field">Which column was wrong, when it is one column.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record ImportRowError(int RowNumber, string? Code, string? Field, string Message);

/// <summary>Exports the catalog as CSV, in the same shape the importer accepts.</summary>
/// <param name="IncludeArchived">Whether archived products are exported.</param>
public sealed record ExportProductsRequest(bool IncludeArchived = false);

/// <param name="Csv">The file contents.</param>
/// <param name="RowCount">How many product rows it holds.</param>
public sealed record ExportProductsResponse(string Csv, int RowCount);

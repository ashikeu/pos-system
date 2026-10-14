using System.Globalization;
using System.Text;
using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.UseCases;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Catalog.UseCases;

/// <summary>
/// Imports products from a CSV file (FR-CAT-09). Validation is row by row and the whole file is
/// checked before anything is written, so a shop either gets a clean import or a report naming
/// every bad row — never half a catalog.
/// </summary>
/// <remarks>
/// <para>
/// A dry run is the default. The first thing anyone does with a supplier's spreadsheet is import it
/// to see what happens, and "see what happens" must not mean "rewrite the prices".
/// </para>
/// <para>
/// Rows are validated against the same aggregates the API uses, not against a parallel set of
/// rules: an import that can create a product the API would reject is how a catalog ends up with
/// records no screen can edit.
/// </para>
/// </remarks>
public sealed class ImportProductsHandler : IUseCase<ImportProductsRequest, ImportProductsResponse>
{
    /// <summary>Columns the importer understands. The header may list them in any order.</summary>
    public static readonly string[] Columns =
    [
        "code",
        "name",
        "name_bn",
        "sku",
        "barcode",
        "barcode_type",
        "base_price",
        "uom",
        "category",
        "brand",
        "tax_class",
        "track_batch",
        "track_serial",
        "track_expiry",
    ];

    private const int MaxRows = 5_000;

    private readonly ICatalogRepository _catalog;
    private readonly IPricingLookup _pricing;
    private readonly IPlanLimitGuard _limits;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public ImportProductsHandler(
        ICatalogRepository catalog,
        IPricingLookup pricing,
        IPlanLimitGuard limits,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _catalog = catalog;
        _pricing = pricing;
        _limits = limits;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<ImportProductsResponse>> HandleAsync(
        ImportProductsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Csv))
        {
            return Error.Validation("catalog.import_empty", "The file is empty.");
        }

        var parse = CsvTable.Parse(request.Csv, Columns);

        if (parse.IsFailure)
        {
            return parse.Error;
        }

        var table = parse.Value;

        if (table.Rows.Count == 0)
        {
            return new ImportProductsResponse(0, 0, 0, 0, request.DryRun, []);
        }

        if (table.Rows.Count > MaxRows)
        {
            return Error.Validation(
                "catalog.import_too_large",
                $"Import at most {MaxRows} rows at a time. Split the file.");
        }

        var context = await LoadReferenceDataAsync(request.DefaultUomCode, cancellationToken).ConfigureAwait(false);
        var errors = new List<ImportRowError>();
        var planned = new List<PlannedRow>();

        // Codes, SKUs and barcodes already used inside this file, so a duplicate within the file is
        // reported as such instead of failing on the unique index halfway through.
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenSkus = new HashSet<string>(StringComparer.Ordinal);
        var seenBarcodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in table.Rows)
        {
            var validated = await ValidateRowAsync(
                row, context, seenCodes, seenSkus, seenBarcodes, request.UpdateExisting, cancellationToken)
                .ConfigureAwait(false);

            if (validated.Error is { } error)
            {
                errors.Add(error);
                continue;
            }

            planned.Add(validated.Row!);
        }

        if (request.DryRun)
        {
            return new ImportProductsResponse(
                table.Rows.Count,
                Created: 0,
                Updated: 0,
                Failed: errors.Count,
                DryRun: true,
                errors);
        }

        if (errors.Count > 0)
        {
            // All or nothing once it is for real: a partial import leaves a shop guessing which
            // half of its spreadsheet landed.
            return Error.Validation(
                "catalog.import_invalid",
                $"{errors.Count} of {table.Rows.Count} rows are not valid. Fix them, or run a dry run to see the report.",
                errors
                    .GroupBy(e => $"row[{e.RowNumber}]", StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal));
        }

        var toCreate = planned.Count(p => p.Existing is null);

        if (toCreate > 0)
        {
            var capacity = await EnsureProductCapacityAsync(toCreate, cancellationToken).ConfigureAwait(false);

            if (capacity.IsFailure)
            {
                return capacity.Error;
            }
        }

        var created = 0;
        var updated = 0;

        foreach (var row in planned)
        {
            if (row.Existing is { } existing)
            {
                Apply(existing, row);
                updated++;
            }
            else
            {
                _catalog.AddProduct(Build(row));
                created++;
            }
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "catalog.products_imported",
                nameof(Product),
                null,
                After: new { Created = created, Updated = updated, Rows = table.Rows.Count }),
            cancellationToken).ConfigureAwait(false);

        return new ImportProductsResponse(table.Rows.Count, created, updated, 0, false, []);
    }

    /// <summary>
    /// Checks the plan's product limit for the whole batch rather than row by row: a file of 200
    /// products on a plan with 50 left must be refused before it writes 50 of them.
    /// </summary>
    private async Task<Result> EnsureProductCapacityAsync(int newProducts, CancellationToken cancellationToken)
    {
        var usage = await _limits.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        var products = usage.FirstOrDefault(u => u.Code == PlanFeatureCode.Products);

        if (products is null || !products.IsCounted)
        {
            return Result.Success();
        }

        if (!products.IsEnabled)
        {
            return Result.Failure(TenancyErrors.FeatureNotInPlan(PlanFeatureCode.Products));
        }

        if (products.Max is { } max && products.Used + newProducts > max)
        {
            return Result.Failure(Error.Conflict(
                "tenancy.plan_limit_reached",
                $"Your plan allows {max} products and this file would make {products.Used + newProducts}. "
                    + "Upgrade the plan or split the file."));
        }

        return Result.Success();
    }

    private async Task<ImportContext> LoadReferenceDataAsync(
        string? defaultUomCode,
        CancellationToken cancellationToken)
    {
        var uoms = (await _catalog.GetUomsAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(u => u.Code, StringComparer.OrdinalIgnoreCase);

        var categories = (await _catalog.GetCategoriesAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase);

        var brands = (await _catalog.GetBrandsAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(b => b.Code, b => b.Id, StringComparer.OrdinalIgnoreCase);

        var taxClasses = await _pricing.GetTaxClassCodesAsync(cancellationToken).ConfigureAwait(false);

        UnitOfMeasure? defaultUom = null;

        if (!string.IsNullOrWhiteSpace(defaultUomCode))
        {
            uoms.TryGetValue(defaultUomCode.Trim(), out defaultUom);
        }

        defaultUom ??= uoms.Values.FirstOrDefault(u => u.Kind == UomKind.Count && u.DecimalPlaces == 0)
            ?? uoms.Values.FirstOrDefault();

        return new ImportContext(uoms, categories, brands, taxClasses, defaultUom);
    }

    private async Task<(ImportRowError? Error, PlannedRow? Row)> ValidateRowAsync(
        CsvRow row,
        ImportContext context,
        HashSet<string> seenCodes,
        HashSet<string> seenSkus,
        HashSet<string> seenBarcodes,
        bool updateExisting,
        CancellationToken cancellationToken)
    {
        ImportRowError Fail(string field, string message) => new(row.Number, row["code"], field, message);

        var code = (row["code"] ?? string.Empty).Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(code))
        {
            return (Fail("code", "A product code is required."), null);
        }

        var name = (row["name"] ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(name))
        {
            return (Fail("name", "A product name is required."), null);
        }

        if (!seenCodes.Add(code))
        {
            return (Fail("code", $"Code '{code}' appears more than once in this file."), null);
        }

        if (!TryParseMoney(row["base_price"], out var basePrice))
        {
            return (Fail("base_price", "A base price is required and must be a number, e.g. 125.50."), null);
        }

        if (basePrice < 0m)
        {
            return (Fail("base_price", "A base price must not be negative."), null);
        }

        var uomCode = (row["uom"] ?? string.Empty).Trim();
        UnitOfMeasure? uom;

        if (string.IsNullOrEmpty(uomCode))
        {
            uom = context.DefaultUom;

            if (uom is null)
            {
                return (Fail("uom", "This organization has no units of measure yet. Create one first."), null);
            }
        }
        else if (!context.Uoms.TryGetValue(uomCode, out uom))
        {
            return (Fail("uom", $"Unknown unit '{uomCode}'. Create it first, or leave the column blank."), null);
        }

        Guid? categoryId = null;
        var categoryCode = (row["category"] ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(categoryCode))
        {
            if (!context.Categories.TryGetValue(categoryCode, out var found))
            {
                return (Fail("category", $"Unknown category '{categoryCode}'."), null);
            }

            categoryId = found;
        }

        Guid? brandId = null;
        var brandCode = (row["brand"] ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(brandCode))
        {
            if (!context.Brands.TryGetValue(brandCode, out var found))
            {
                return (Fail("brand", $"Unknown brand '{brandCode}'."), null);
            }

            brandId = found;
        }

        Guid? taxClassId = null;
        var taxClassCode = (row["tax_class"] ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(taxClassCode))
        {
            if (!context.TaxClasses.TryGetValue(taxClassCode, out var found))
            {
                return (Fail("tax_class", $"Unknown tax class '{taxClassCode}'."), null);
            }

            taxClassId = found;
        }

        var sku = (row["sku"] ?? string.Empty).Trim().ToUpperInvariant();
        sku = string.IsNullOrEmpty(sku) ? code : sku;

        if (!seenSkus.Add(sku))
        {
            return (Fail("sku", $"SKU '{sku}' appears more than once in this file."), null);
        }

        var barcode = (row["barcode"] ?? string.Empty).Trim();
        var barcodeType = BarcodeType.Ean13;

        if (!string.IsNullOrEmpty(barcode))
        {
            barcode = barcode.ToUpperInvariant();

            var typeText = (row["barcode_type"] ?? string.Empty).Trim();

            if (!string.IsNullOrEmpty(typeText) && !Enum.TryParse(typeText, true, out barcodeType))
            {
                return (Fail(
                    "barcode_type",
                    $"Unknown barcode type '{typeText}'. Use one of: {string.Join(", ", Enum.GetNames<BarcodeType>())}."),
                    null);
            }

            if (string.IsNullOrEmpty(typeText))
            {
                // Guessed from the digits so a file without the column still validates properly
                // rather than failing every 8-digit code against EAN-13.
                barcodeType = barcode.Length switch
                {
                    13 when barcode.All(char.IsAsciiDigit) => BarcodeType.Ean13,
                    12 when barcode.All(char.IsAsciiDigit) => BarcodeType.UpcA,
                    8 when barcode.All(char.IsAsciiDigit) => BarcodeType.Ean8,
                    _ => BarcodeType.Code128,
                };
            }

            if (!Barcode.IsWellFormed(barcode, barcodeType))
            {
                return (Fail(
                    "barcode",
                    $"'{barcode}' is not a valid {barcodeType} code — check the digits, or set barcode_type to Internal."),
                    null);
            }

            if (!seenBarcodes.Add(barcode))
            {
                return (Fail("barcode", $"Barcode '{barcode}' appears more than once in this file."), null);
            }
        }

        var trackBatch = ParseFlag(row["track_batch"]);
        var trackSerial = ParseFlag(row["track_serial"]);
        var trackExpiry = ParseFlag(row["track_expiry"]);

        if (trackExpiry && !trackBatch)
        {
            return (Fail("track_expiry", "Expiry tracking needs track_batch as well: an expiry belongs to a batch."),
                null);
        }

        if (trackBatch && trackSerial)
        {
            return (Fail("track_serial", "Track either batches or serials, not both."), null);
        }

        var existing = await _catalog.FindProductByCodeAsync(code, cancellationToken).ConfigureAwait(false);

        if (existing is not null && !updateExisting)
        {
            return (Fail("code", $"A product with code '{code}' already exists. Enable updates to change it."), null);
        }

        if (existing is null
            && await _catalog.SkuExistsAsync(sku, null, cancellationToken).ConfigureAwait(false))
        {
            return (Fail("sku", $"SKU '{sku}' is already used by another product."), null);
        }

        if (!string.IsNullOrEmpty(barcode)
            && await _catalog.BarcodeExistsAsync(barcode, null, cancellationToken).ConfigureAwait(false))
        {
            var owner = await _catalog.LookupAsync(barcode, cancellationToken).ConfigureAwait(false);

            if (existing is null || owner?.ProductId != existing.Id)
            {
                return (Fail("barcode", $"Barcode '{barcode}' is already used by another product."), null);
            }
        }

        return (null, new PlannedRow(
            row.Number,
            code,
            name,
            (row["name_bn"] ?? string.Empty).Trim(),
            sku,
            basePrice,
            uom.Id,
            categoryId,
            brandId,
            taxClassId,
            barcode,
            barcodeType,
            trackBatch,
            trackSerial,
            trackExpiry,
            existing));
    }

    private Product Build(PlannedRow row)
    {
        var product = Product.Create(
            _guids.NewId(),
            _tenant.OrganizationId,
            row.Code,
            row.Name,
            row.UomId);

        product.UpdateDetails(row.Name, string.IsNullOrEmpty(row.NameBn) ? null : row.NameBn, null);
        product.Classify(row.CategoryId, row.BrandId, row.TaxClassId);
        product.SetTrackingPolicy(true, row.TrackBatch, row.TrackSerial, row.TrackExpiry);

        var variant = product.AddVariant(_guids.NewId(), row.Sku, null, row.BasePrice, row.UomId);

        if (!string.IsNullOrEmpty(row.Barcode))
        {
            variant.AddBarcode(_guids.NewId(), row.Barcode, row.BarcodeType);
        }

        product.Activate();
        return product;
    }

    private void Apply(Product product, PlannedRow row)
    {
        product.UpdateDetails(row.Name, string.IsNullOrEmpty(row.NameBn) ? null : row.NameBn, product.Description);
        product.Classify(row.CategoryId, row.BrandId, row.TaxClassId);
        product.SetTrackingPolicy(
            product.ProductType == ProductType.Standard,
            row.TrackBatch,
            row.TrackSerial,
            row.TrackExpiry);

        var variant = product.Variants.FirstOrDefault(v => string.Equals(v.Sku, row.Sku, StringComparison.Ordinal))
            ?? product.Variants.FirstOrDefault(v => v.IsDefault);

        if (variant is null)
        {
            variant = product.AddVariant(_guids.NewId(), row.Sku, null, row.BasePrice, row.UomId);
        }
        else
        {
            variant.ChangeBasePrice(row.BasePrice);
        }

        if (!string.IsNullOrEmpty(row.Barcode)
            && !variant.Barcodes.Any(b => string.Equals(b.Code, row.Barcode, StringComparison.Ordinal)))
        {
            variant.AddBarcode(_guids.NewId(), row.Barcode, row.BarcodeType);
        }
    }

    private static bool TryParseMoney(string? text, out decimal value) =>
        decimal.TryParse(
            (text ?? string.Empty).Trim(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Reads a boolean column the way a spreadsheet writes one: <c>1</c>, <c>yes</c>, <c>y</c>,
    /// <c>true</c> are all true, and anything else — including blank — is false.
    /// </summary>
    private static bool ParseFlag(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();

        return trimmed.Length > 0
            && (trimmed.Equals("1", StringComparison.Ordinal)
                || trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ImportContext(
        Dictionary<string, UnitOfMeasure> Uoms,
        Dictionary<string, Guid> Categories,
        Dictionary<string, Guid> Brands,
        IReadOnlyDictionary<string, Guid> TaxClasses,
        UnitOfMeasure? DefaultUom);

    private sealed record PlannedRow(
        int RowNumber,
        string Code,
        string Name,
        string NameBn,
        string Sku,
        decimal BasePrice,
        Guid UomId,
        Guid? CategoryId,
        Guid? BrandId,
        Guid? TaxClassId,
        string Barcode,
        BarcodeType BarcodeType,
        bool TrackBatch,
        bool TrackSerial,
        bool TrackExpiry,
        Product? Existing);
}

/// <summary>Exports the catalog as CSV in the shape the importer accepts (FR-CAT-10).</summary>
public sealed class ExportProductsHandler : IUseCase<ExportProductsRequest, ExportProductsResponse>
{
    private readonly ICatalogRepository _catalog;
    private readonly IPricingLookup _pricing;

    public ExportProductsHandler(ICatalogRepository catalog, IPricingLookup pricing)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pricing);

        _catalog = catalog;
        _pricing = pricing;
    }

    public async Task<Result<ExportProductsResponse>> HandleAsync(
        ExportProductsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uoms = (await _catalog.GetUomsAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(u => u.Id, u => u.Code);
        var categories = (await _catalog.GetCategoriesAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Id, c => c.Code);
        var brands = (await _catalog.GetBrandsAsync(true, cancellationToken).ConfigureAwait(false))
            .ToDictionary(b => b.Id, b => b.Code);
        var taxClasses = (await _pricing.GetTaxClassCodesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(pair => pair.Value, pair => pair.Key);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', ImportProductsHandler.Columns));

        var rows = 0;
        var page = new PageRequest(1, PageRequest.MaxPageSize);

        // Paged rather than loaded whole: an export is the one read that is allowed to be slow, but
        // it is not allowed to hold a tenant's entire catalog in memory at once.
        while (true)
        {
            var batch = await _catalog
                .SearchProductsAsync(
                    new ProductSearchCriteria(page, IncludeArchived: request.IncludeArchived),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in batch.Items)
            {
                var product = await _catalog
                    .FindProductAsync(item.ProductId, cancellationToken)
                    .ConfigureAwait(false);

                if (product is null)
                {
                    continue;
                }

                foreach (var variant in product.Variants)
                {
                    var primaryBarcode = variant.Barcodes.FirstOrDefault(b => b.IsPrimary)
                        ?? variant.Barcodes.FirstOrDefault();

                    builder.AppendLine(string.Join(',', new[]
                    {
                        CsvTable.Escape(product.Code),
                        CsvTable.Escape(product.Name),
                        CsvTable.Escape(product.NameBn),
                        CsvTable.Escape(variant.Sku),
                        CsvTable.Escape(primaryBarcode?.Code),
                        CsvTable.Escape(primaryBarcode?.Type.ToString()),
                        variant.BasePrice.ToString(CultureInfo.InvariantCulture),
                        CsvTable.Escape(uoms.TryGetValue(variant.UomId, out var uom) ? uom : null),
                        CsvTable.Escape(product.CategoryId is { } c && categories.TryGetValue(c, out var cat)
                            ? cat
                            : null),
                        CsvTable.Escape(product.BrandId is { } b && brands.TryGetValue(b, out var brand)
                            ? brand
                            : null),
                        CsvTable.Escape(product.TaxClassId is { } t && taxClasses.TryGetValue(t, out var tax)
                            ? tax
                            : null),
                        product.TrackBatch ? "1" : "0",
                        product.TrackSerial ? "1" : "0",
                        product.TrackExpiry ? "1" : "0",
                    }));

                    rows++;
                }
            }

            if (!batch.HasMore)
            {
                break;
            }

            page = page with { Page = page.Page + 1 };
        }

        return new ExportProductsResponse(builder.ToString(), rows);
    }
}

/// <summary>
/// The pricing facts the catalog import and export need, as a contract rather than a direct read
/// of pricing tables: modules meet at their contracts (§3 of CLAUDE.md).
/// </summary>
public interface IPricingLookup
{
    /// <summary>Tax class codes to ids, for resolving the <c>tax_class</c> column.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetTaxClassCodesAsync(CancellationToken cancellationToken = default);
}

/// <summary>One parsed CSV row, addressable by column name.</summary>
/// <param name="Number">1-based row number in the file, counting the header.</param>
/// <param name="Values">Column values by lower-case column name.</param>
public sealed record CsvRow(int Number, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The value of a column, or null when the file does not have it.</summary>
    public string? this[string column] => Values.TryGetValue(column, out var value) ? value : null;
}

/// <summary>A parsed CSV file: the header it declared and the rows under it.</summary>
/// <param name="Header">Column names, lower-cased.</param>
/// <param name="Rows">The data rows.</param>
public sealed record CsvTable(IReadOnlyList<string> Header, IReadOnlyList<CsvRow> Rows)
{
    /// <summary>
    /// Parses a CSV file: quoted fields, embedded commas, embedded quotes doubled, CRLF or LF, and
    /// an optional UTF-8 BOM — the things Excel actually produces.
    /// </summary>
    /// <param name="csv">The file contents.</param>
    /// <param name="knownColumns">
    /// Columns the caller understands. A header naming none of them is rejected, because it is
    /// usually a file exported in a different shape and importing it would produce one error per row.
    /// </param>
    public static Result<CsvTable> Parse(string csv, IReadOnlyCollection<string> knownColumns)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(knownColumns);

        var text = csv.TrimStart('﻿');
        var lines = SplitRecords(text);

        if (lines.Count == 0)
        {
            return Error.Validation("catalog.import_empty", "The file has no rows.");
        }

        var header = ParseLine(lines[0])
            .Select(column => column.Trim().ToLowerInvariant().Replace(' ', '_'))
            .ToArray();

        if (!header.Any(knownColumns.Contains))
        {
            return Error.Validation(
                "catalog.import_header_unrecognised",
                $"The header row names none of the expected columns. Expected at least one of: "
                    + string.Join(", ", knownColumns));
        }

        var rows = new List<CsvRow>();

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = ParseLine(line);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var column = 0; column < header.Length; column++)
            {
                values[header[column]] = column < cells.Count ? cells[column] : string.Empty;
            }

            rows.Add(new CsvRow(i + 1, values));
        }

        return new CsvTable(header, rows);
    }

    /// <summary>Characters that force a field to be quoted on output.</summary>
    private static readonly System.Buffers.SearchValues<char> NeedsQuoting =
        System.Buffers.SearchValues.Create(",\"\r\n");

    /// <summary>Quotes a value for CSV output when it needs quoting.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(NeedsQuoting) >= 0
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
    }

    /// <summary>Splits into records, keeping newlines that sit inside quoted fields.</summary>
    private static List<string> SplitRecords(string text)
    {
        var records = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];

            switch (character)
            {
                case '"':
                    inQuotes = !inQuotes;
                    current.Append(character);
                    break;

                case '\r' when !inQuotes:
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    records.Add(current.ToString());
                    current.Clear();
                    break;

                case '\n' when !inQuotes:
                    records.Add(current.ToString());
                    current.Clear();
                    break;

                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0)
        {
            records.Add(current.ToString());
        }

        return records;
    }

    private static List<string> ParseLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    cells.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}

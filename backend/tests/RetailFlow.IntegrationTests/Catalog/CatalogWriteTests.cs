using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Catalog.UseCases;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Pricing;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Catalog;

/// <summary>
/// Catalog writes against a real database: the rules that only a database can prove — uniqueness
/// per tenant, archival instead of deletion, and an import that either lands whole or not at all.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CatalogWriteTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private TestTenant _tenant = null!;
    private CatalogFixtureData _data = null!;

    public CatalogWriteTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture);
        _tenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("CAT"));
        _data = await CatalogTestData.SeedReferenceDataAsync(_fixture, _tenant.OrganizationId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Code(string prefix) =>
        prefix + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);

    private AsyncServiceScope Scope() =>
        _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);

    [Fact]
    public async Task A_product_created_through_the_use_case_is_found_by_its_sku_and_by_its_code()
    {
        var code = Code("P");
        var sku = Code("SKU");

        await using var scope = Scope();
        var create = scope.ServiceProvider.GetRequiredService<IUseCase<CreateProductRequest, ProductResponse>>();

        var created = await create.HandleAsync(new CreateProductRequest(
            code,
            "Mustard oil 1L",
            BasePrice: 320m,
            BaseUomId: _data.PieceUomId,
            Sku: sku,
            NameBn: "সরিষার তেল ১ লিটার",
            CategoryId: _data.CategoryId,
            BrandId: _data.BrandId,
            TaxClassId: _data.StandardTaxClassId));

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : string.Empty);
        created.Value.Status.Should().Be(CatalogStatus.Active);
        created.Value.Variants.Should().HaveCount(1);

        var lookup = scope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var bySku = await lookup.HandleAsync(new LookupProductRequest(sku));
        bySku.IsSuccess.Should().BeTrue(bySku.IsFailure ? bySku.Error.ToString() : string.Empty);
        bySku.Value.Product.ProductId.Should().Be(created.Value.ProductId);
        bySku.Value.Product.MatchedBarcode.Should().BeNull("nothing was scanned");

        // Typed from a label or read out over the phone, in either case.
        var byCode = await lookup.HandleAsync(new LookupProductRequest(code.ToLowerInvariant()));
        byCode.IsSuccess.Should().BeTrue(byCode.IsFailure ? byCode.Error.ToString() : string.Empty);
        byCode.Value.Product.VariantId.Should().Be(bySku.Value.Product.VariantId);

        var duplicate = await create.HandleAsync(new CreateProductRequest(
            code.ToLowerInvariant(), "Another product", 10m, _data.PieceUomId));

        duplicate.IsFailure.Should().BeTrue("a product code is unique per tenant");
        duplicate.Error.Code.Should().Be("catalog.product_code_taken");
    }

    [Fact]
    public async Task A_barcode_belongs_to_one_variant_per_tenant_and_another_tenant_may_reuse_it()
    {
        const string Shared = "4015000010122";

        var (_, firstVariantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 25m, barcode: Shared);
        var (_, secondVariantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 30m);

        await using (var scope = Scope())
        {
            var addBarcode = scope.ServiceProvider
                .GetRequiredService<IUseCase<AddBarcodeRequest, ProductResponse>>();

            var clash = await addBarcode.HandleAsync(new AddBarcodeRequest(secondVariantId, Shared));

            clash.IsFailure.Should().BeTrue("one code scans to one thing, or the till is guessing");
            clash.Error.Code.Should().Be("catalog.barcode_taken");
        }

        // The code still belongs to the variant that had it, unchanged by the refused attempt.
        await using (var scope = Scope())
        {
            var lookup = scope.ServiceProvider
                .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

            var scanned = await lookup.HandleAsync(new LookupProductRequest(Shared));
            scanned.Value.Product.VariantId.Should().Be(firstVariantId);
        }

        // Two shops selling the same manufactured product scan the same EAN; uniqueness is per
        // tenant, never global (ADR-0004).
        var other = await IdentityTestData.CreateTenantAsync(_fixture, Code("OTH"));
        var otherData = await CatalogTestData.SeedReferenceDataAsync(_fixture, other.OrganizationId);
        var (_, otherVariantId) = await CatalogTestData.AddProductAsync(
            _fixture, other.OrganizationId, otherData, Code("P"), basePrice: 27m, barcode: Shared);

        await using var otherScope = _fixture.CreateScope(
            other.OrganizationId, other.OwnerUserId, other.BranchId);
        var otherLookup = otherScope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var otherScan = await otherLookup.HandleAsync(new LookupProductRequest(Shared));
        otherScan.IsSuccess.Should().BeTrue(otherScan.IsFailure ? otherScan.Error.ToString() : string.Empty);
        otherScan.Value.Product.VariantId.Should().Be(otherVariantId);
    }

    [Fact]
    public async Task A_sku_lookup_sells_a_single_unit_even_when_the_variant_also_has_a_case_barcode()
    {
        var sku = Code("SKU");
        var caseCode = "CASE-" + Code("C");

        await using var scope = Scope();
        var create = scope.ServiceProvider.GetRequiredService<IUseCase<CreateProductRequest, ProductResponse>>();

        var created = await create.HandleAsync(new CreateProductRequest(
            Code("P"), "Soap bar", BasePrice: 40m, BaseUomId: _data.PieceUomId, Sku: sku));
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : string.Empty);

        var variantId = created.Value.Variants[0].VariantId;

        var addBarcode = scope.ServiceProvider
            .GetRequiredService<IUseCase<AddBarcodeRequest, ProductResponse>>();
        var added = await addBarcode.HandleAsync(new AddBarcodeRequest(
            variantId, caseCode, BarcodeType.Internal, PackQuantity: 48m));
        added.IsSuccess.Should().BeTrue(added.IsFailure ? added.Error.ToString() : string.Empty);

        var lookup = scope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var typed = await lookup.HandleAsync(new LookupProductRequest(sku));

        typed.Value.Product.PackQuantity.Should().Be(
            1m,
            "the case barcode was not the thing scanned, so it must not set the quantity");
        typed.Value.Line.Quantity.Should().Be(1m);
        typed.Value.Line.NetOfDiscount.Should().Be(40m);

        var scanned = await lookup.HandleAsync(new LookupProductRequest(caseCode));
        scanned.Value.Product.PackQuantity.Should().Be(48m);
    }

    [Fact]
    public async Task An_archived_product_leaves_the_catalog_search_but_keeps_its_rows()
    {
        var code = Code("P");
        var (productId, _) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, code, basePrice: 60m);

        await using var scope = Scope();
        var search = scope.ServiceProvider
            .GetRequiredService<IUseCase<SearchProductsRequest, PagedResult<ProductListItem>>>();

        var before = await search.HandleAsync(new SearchProductsRequest(new PageRequest(), Search: code));
        before.Value.Items.Should().ContainSingle(i => i.ProductId == productId);

        var setStatus = scope.ServiceProvider
            .GetRequiredService<IUseCase<SetProductStatusRequest, ProductResponse>>();
        var archived = await setStatus.HandleAsync(new SetProductStatusRequest(productId, CatalogStatus.Archived));
        archived.IsSuccess.Should().BeTrue(archived.IsFailure ? archived.Error.ToString() : string.Empty);

        var after = await search.HandleAsync(new SearchProductsRequest(new PageRequest(), Search: code));
        after.Value.Items.Should().BeEmpty("an archived line is out of the way, not out of the database");

        var withArchived = await search.HandleAsync(
            new SearchProductsRequest(new PageRequest(), Search: code, IncludeArchived: true));
        withArchived.Value.Items.Should().ContainSingle(i => i.ProductId == productId);

        // Still loadable, because last year's sales still point at it (NFR-09).
        var get = scope.ServiceProvider.GetRequiredService<IUseCase<GetProductRequest, ProductResponse>>();
        var loaded = await get.HandleAsync(new GetProductRequest(productId));
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Status.Should().Be(CatalogStatus.Archived);
    }

    [Fact]
    public async Task A_dry_run_names_every_bad_row_and_writes_nothing()
    {
        var goodCode = Code("IMP");
        var csv = string.Join(
            '\n',
            "code,name,sku,barcode,base_price,uom,category,brand,tax_class,track_batch,track_expiry",
            $"{goodCode},Good row,{goodCode},,125.50,PCS,GROCERY,ACME,STD,0,0",
            ",Missing code,,,10,PCS,,,,0,0",
            $"{Code("IMP")},Bad price,,,not-a-number,PCS,,,,0,0",
            $"{Code("IMP")},Unknown unit,,,10,NOPE,,,,0,0",
            $"{Code("IMP")},Bad barcode,,4015000010123,10,PCS,,,,0,0",
            $"{Code("IMP")},Expiry without batch,,,10,PCS,,,,0,1");

        await using var scope = Scope();
        var import = scope.ServiceProvider
            .GetRequiredService<IUseCase<ImportProductsRequest, ImportProductsResponse>>();

        var result = await import.HandleAsync(new ImportProductsRequest(csv));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);

        var report = result.Value;
        report.DryRun.Should().BeTrue("a dry run is the default, so nobody rewrites prices by accident");
        report.TotalRows.Should().Be(6);
        report.Created.Should().Be(0);
        report.Updated.Should().Be(0);
        report.Failed.Should().Be(5);
        report.Errors.Should().HaveCount(5);

        // The row numbers are the ones the spreadsheet shows, header included.
        report.Errors.Select(e => e.RowNumber).Should().BeEquivalentTo(new[] { 3, 4, 5, 6, 7 });
        report.Errors.Should().Contain(e => e.Field == "code");
        report.Errors.Should().Contain(e => e.Field == "base_price");
        report.Errors.Should().Contain(e => e.Field == "uom");
        report.Errors.Should().Contain(e => e.Field == "barcode");
        report.Errors.Should().Contain(e => e.Field == "track_expiry");

        var search = scope.ServiceProvider
            .GetRequiredService<IUseCase<SearchProductsRequest, PagedResult<ProductListItem>>>();
        var found = await search.HandleAsync(
            new SearchProductsRequest(new PageRequest(), Search: goodCode, IncludeArchived: true));

        found.Value.Items.Should().BeEmpty("a dry run writes nothing, not even the rows that passed");
    }

    [Fact]
    public async Task An_import_with_one_bad_row_writes_none_of_the_file()
    {
        var goodCode = Code("IMP");
        var csv = string.Join(
            '\n',
            "code,name,base_price,uom",
            $"{goodCode},Good row,125.50,PCS",
            $"{Code("IMP")},Bad price,oops,PCS");

        await using var scope = Scope();
        var import = scope.ServiceProvider
            .GetRequiredService<IUseCase<ImportProductsRequest, ImportProductsResponse>>();

        var result = await import.HandleAsync(new ImportProductsRequest(csv, DryRun: false));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("catalog.import_invalid");
        result.Error.FieldErrors.Should().NotBeNull().And.ContainKey("row[3]");

        var search = scope.ServiceProvider
            .GetRequiredService<IUseCase<SearchProductsRequest, PagedResult<ProductListItem>>>();
        var found = await search.HandleAsync(
            new SearchProductsRequest(new PageRequest(), Search: goodCode, IncludeArchived: true));

        found.Value.Items.Should().BeEmpty("half a catalog is worse than none");
    }

    [Fact]
    public async Task A_clean_file_imports_and_the_export_hands_the_same_rows_back()
    {
        var first = Code("IMP");
        var second = Code("IMP");
        var barcode = "4015000010122";

        var csv = string.Join(
            '\n',
            "code,name,name_bn,sku,barcode,base_price,uom,category,brand,tax_class,track_batch,track_expiry",
            $"{first},Rice 5kg,চাল ৫ কেজি,{first},{barcode},450,PCS,GROCERY,ACME,STD,0,0",
            $"{second},Paracetamol 500mg,,{second},,12.75,PCS,GROCERY,,STD,1,1");

        await using var scope = Scope();
        var import = scope.ServiceProvider
            .GetRequiredService<IUseCase<ImportProductsRequest, ImportProductsResponse>>();

        var imported = await import.HandleAsync(new ImportProductsRequest(csv, DryRun: false));

        imported.IsSuccess.Should().BeTrue(imported.IsFailure ? imported.Error.ToString() : string.Empty);
        imported.Value.Created.Should().Be(2);
        imported.Value.Failed.Should().Be(0);

        // Re-importing the same file is refused rather than creating a second copy; the importer
        // only changes products when it is told to.
        var again = await import.HandleAsync(new ImportProductsRequest(csv, DryRun: false));
        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("catalog.import_invalid");

        var priced = await import.HandleAsync(new ImportProductsRequest(
            csv.Replace("450,PCS", "470,PCS", StringComparison.Ordinal),
            DryRun: false,
            UpdateExisting: true));

        priced.IsSuccess.Should().BeTrue(priced.IsFailure ? priced.Error.ToString() : string.Empty);
        priced.Value.Updated.Should().Be(2);
        priced.Value.Created.Should().Be(0);

        var lookup = scope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var scanned = await lookup.HandleAsync(new LookupProductRequest(barcode));
        scanned.IsSuccess.Should().BeTrue(scanned.IsFailure ? scanned.Error.ToString() : string.Empty);
        scanned.Value.Price.UnitPrice.Should().Be(470m, "the second pass updated the price");
        scanned.Value.Tax.VatRate.Should().Be(CatalogTestData.StandardVatRate, "the STD class came from the file");

        var batchTracked = await lookup.HandleAsync(new LookupProductRequest(second));
        batchTracked.Value.Product.TrackBatch.Should().BeTrue();
        batchTracked.Value.Product.TrackExpiry.Should().BeTrue();

        var export = scope.ServiceProvider
            .GetRequiredService<IUseCase<ExportProductsRequest, ExportProductsResponse>>();
        var exported = await export.HandleAsync(new ExportProductsRequest());

        exported.IsSuccess.Should().BeTrue();
        exported.Value.Csv.Should().StartWith(string.Join(',', ImportProductsHandler.Columns));
        exported.Value.Csv.Should().Contain(first).And.Contain(second).And.Contain(barcode);
        exported.Value.RowCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_price_tier_can_be_added_to_a_saved_list_and_then_corrected()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        await using var scope = Scope();
        var setItems = scope.ServiceProvider
            .GetRequiredService<IUseCase<SetPriceListItemsRequest, SetPriceListItemsResponse>>();
        var resolve = scope.ServiceProvider
            .GetRequiredService<IUseCase<ResolvePriceRequest, ResolvePriceResponse>>();

        // The list was saved before this request, so its new item is an insert into an existing
        // aggregate — the case that silently became an UPDATE while EF believed the database
        // generated the keys.
        var added = await setItems.HandleAsync(new SetPriceListItemsRequest(
            _data.DefaultPriceListId,
            [new PriceListItemInput(variantId, 110m), new PriceListItemInput(variantId, 99m, MinQuantity: 12m)]));

        added.IsSuccess.Should().BeTrue(added.IsFailure ? added.Error.ToString() : string.Empty);
        added.Value.ItemsSet.Should().Be(2);

        var single = await resolve.HandleAsync(new ResolvePriceRequest(variantId));
        single.Value.UnitPrice.Should().Be(110m);
        single.Value.Source.Should().Be(PriceSource.DefaultPriceList);

        var dozen = await resolve.HandleAsync(new ResolvePriceRequest(variantId, Quantity: 12m));
        dozen.Value.UnitPrice.Should().Be(99m);

        var corrected = await setItems.HandleAsync(new SetPriceListItemsRequest(
            _data.DefaultPriceListId,
            [new PriceListItemInput(variantId, 105m)]));

        corrected.IsSuccess.Should().BeTrue(corrected.IsFailure ? corrected.Error.ToString() : string.Empty);

        var afterCorrection = await resolve.HandleAsync(new ResolvePriceRequest(variantId));
        afterCorrection.Value.UnitPrice.Should().Be(105m, "the tier was replaced, not duplicated");

        var dozenStillThere = await resolve.HandleAsync(new ResolvePriceRequest(variantId, Quantity: 12m));
        dozenStillThere.Value.UnitPrice.Should().Be(99m, "setting one tier leaves the others alone");
    }
}

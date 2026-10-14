using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Pricing.Abstractions;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Domain.Pricing;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Catalog;

/// <summary>
/// Price and tax resolution against a real database: the documented order in §1 and the versioned,
/// effective-dated rates in §2 of <c>docs/02-business-rules.md</c>. The unit tests prove the
/// arithmetic; these prove the right rows reach it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PricingResolutionTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private TestTenant _tenant = null!;
    private CatalogFixtureData _data = null!;

    public PricingResolutionTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture).ConfigureAwait(false);
        _tenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("PRC")).ConfigureAwait(false);
        _data = await CatalogTestData.SeedReferenceDataAsync(_fixture, _tenant.OrganizationId).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Code(string prefix) =>
        prefix + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task With_no_price_list_entry_the_variant_base_price_is_used()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();

        var resolved = await resolver.ResolveAsync(new PriceQuery(variantId));

        resolved.UnitPrice.Should().Be(125m);
        resolved.Source.Should().Be(PriceSource.VariantBasePrice);
    }

    [Fact]
    public async Task The_default_price_list_beats_the_base_price_and_a_branch_override_beats_both()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        await CatalogTestData.SetListPriceAsync(
            _fixture, _tenant.OrganizationId, _data.DefaultPriceListId, variantId, 110m);

        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();
            var resolved = await resolver.ResolveAsync(new PriceQuery(variantId, BranchId: _tenant.BranchId));

            resolved.UnitPrice.Should().Be(110m);
            resolved.Source.Should().Be(PriceSource.DefaultPriceList);
        }

        await CatalogTestData.AddBranchPriceAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, variantId, 99m);

        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();
            var resolved = await resolver.ResolveAsync(new PriceQuery(variantId, BranchId: _tenant.BranchId));

            resolved.UnitPrice.Should().Be(99m);
            resolved.Source.Should().Be(PriceSource.BranchPriceOverride);
        }
    }

    [Fact]
    public async Task A_branch_override_does_not_apply_at_another_branch()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        await CatalogTestData.AddBranchPriceAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, variantId, 99m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId);
        var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();

        var atSecond = await resolver.ResolveAsync(new PriceQuery(variantId, BranchId: _tenant.SecondBranchId));

        atSecond.UnitPrice.Should().Be(125m, "the other branch keeps the base price");
        atSecond.Source.Should().Be(PriceSource.VariantBasePrice);
    }

    [Fact]
    public async Task A_customer_group_list_beats_a_branch_override()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        var groupId = Guid.NewGuid();
        await CatalogTestData.AddBranchPriceAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, variantId, 99m);
        await CatalogTestData.AddGroupPriceListAsync(
            _fixture, _tenant.OrganizationId, groupId, variantId, 90m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();

        var forGroup = await resolver.ResolveAsync(
            new PriceQuery(variantId, BranchId: _tenant.BranchId, CustomerGroupId: groupId));
        forGroup.UnitPrice.Should().Be(90m);
        forGroup.Source.Should().Be(PriceSource.CustomerGroupPriceList);

        // A customer who is not in that group must not get its price.
        var walkIn = await resolver.ResolveAsync(new PriceQuery(variantId, BranchId: _tenant.BranchId));
        walkIn.UnitPrice.Should().Be(99m);
    }

    [Fact]
    public async Task A_manual_override_beats_everything()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        await CatalogTestData.AddBranchPriceAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, variantId, 99m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();

        var resolved = await resolver.ResolveAsync(
            new PriceQuery(variantId, BranchId: _tenant.BranchId, ManualPrice: 80m));

        resolved.UnitPrice.Should().Be(80m);
        resolved.Source.Should().Be(PriceSource.ManualOverride);
    }

    [Fact]
    public async Task A_wholesale_tier_applies_from_its_minimum_quantity()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 25m);

        await CatalogTestData.SetListPriceAsync(
            _fixture, _tenant.OrganizationId, _data.DefaultPriceListId, variantId, 20m);
        await CatalogTestData.SetListPriceAsync(
            _fixture, _tenant.OrganizationId, _data.DefaultPriceListId, variantId, 18m, minQuantity: 12m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();

        (await resolver.ResolveAsync(new PriceQuery(variantId, 6m))).UnitPrice.Should().Be(20m);
        (await resolver.ResolveAsync(new PriceQuery(variantId, 12m))).UnitPrice.Should().Be(18m);
        (await resolver.ResolveAsync(new PriceQuery(variantId, 240m))).UnitPrice.Should().Be(18m);
    }

    [Fact]
    public async Task An_expired_price_list_stops_applying()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 125m);

        await CatalogTestData.SetListPriceAsync(
            _fixture, _tenant.OrganizationId, _data.DefaultPriceListId, variantId, 110m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var resolver = scope.ServiceProvider.GetRequiredService<IPriceResolver>();

        var now = await resolver.ResolveAsync(new PriceQuery(variantId));
        now.UnitPrice.Should().Be(110m);

        // Resolving as of a date before the list existed is how a sync replay prices an old sale.
        var longAgo = await resolver.ResolveAsync(
            new PriceQuery(variantId, AsOfUtc: new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        longAgo.UnitPrice.Should().Be(110m, "the list has no window, so it applies at any date");
    }

    [Fact]
    public async Task Tax_resolves_from_the_version_in_force_at_the_instant_asked_about()
    {
        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var taxes = scope.ServiceProvider.GetRequiredService<ITaxResolver>();

        var before = await taxes.ResolveAsync(
            _data.StandardTaxClassId,
            null,
            new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));

        before.Treatment.Should().Be(TaxTreatment.Exempt);
        before.ExemptionReason.Should().Contain("No tax rate in force",
            "charging a rate that was not yet in force would be worse than refusing");

        var current = await taxes.ResolveAsync(_data.StandardTaxClassId);
        current.Treatment.Should().Be(TaxTreatment.Exclusive);
        current.VatRate.Should().Be(CatalogTestData.StandardVatRate);
        current.TaxRuleVersionId.Should().NotBeNull("the sale line stores the exact version it used");
    }

    [Fact]
    public async Task A_new_rate_version_applies_from_its_date_and_the_old_one_still_prices_the_past()
    {
        var changeDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await CatalogTestData.AddTaxVersionAsync(
            _fixture, _tenant.OrganizationId, _data.StandardTaxClassId, 0.175m, 0m, changeDate);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var taxes = scope.ServiceProvider.GetRequiredService<ITaxResolver>();

        var beforeChange = await taxes.ResolveAsync(
            _data.StandardTaxClassId, null, changeDate.AddDays(-1));
        beforeChange.VatRate.Should().Be(0.15m, "a filed return must stay reproducible");

        var afterChange = await taxes.ResolveAsync(_data.StandardTaxClassId, null, changeDate);
        afterChange.VatRate.Should().Be(0.175m);
        afterChange.TaxRuleVersionId.Should().NotBeNull();
        (afterChange.TaxRuleVersionId == beforeChange.TaxRuleVersionId).Should().BeFalse(
            "a rate change opens a new version rather than editing the old one");
    }

    [Fact]
    public async Task An_exempt_class_charges_nothing_and_keeps_its_reason()
    {
        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var taxes = scope.ServiceProvider.GetRequiredService<ITaxResolver>();

        var snapshot = await taxes.ResolveAsync(_data.ExemptTaxClassId);

        snapshot.Treatment.Should().Be(TaxTreatment.Exempt);
        snapshot.VatRate.Should().Be(0m);
        snapshot.ExemptionReason.Should().Be("SRO 100/2026 schedule 1");
    }

    [Fact]
    public async Task A_product_with_no_tax_class_falls_back_to_the_tenant_default()
    {
        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 100m, taxClassId: null);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var lookup = scope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var result = await lookup.HandleAsync(new LookupProductRequest(
            (await CatalogVariantSku(scope.ServiceProvider, variantId))!));

        result.IsSuccess.Should().BeTrue();
        result.Value.Tax.VatRate.Should().Be(CatalogTestData.StandardVatRate);
        result.Value.Line.VatAmount.Should().Be(15m);
    }

    [Fact]
    public async Task The_scan_path_returns_the_product_the_price_and_the_tax_in_one_call()
    {
        var barcode = "4015000010122";
        var (productId, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 200m, barcode: barcode);

        await CatalogTestData.AddBranchPriceAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, variantId, 180m);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var lookup = scope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var result = await lookup.HandleAsync(new LookupProductRequest(barcode, Quantity: 2m));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);

        var response = result.Value;
        response.Product.ProductId.Should().Be(productId);
        response.Product.VariantId.Should().Be(variantId);
        response.Product.MatchedBarcode.Should().Be(barcode);
        response.Product.IsSellable.Should().BeTrue();

        response.Price.UnitPrice.Should().Be(180m);
        response.Price.Source.Should().Be(PriceSource.BranchPriceOverride);

        // 2 × 180 = 360 net, 15% VAT = 54, total 414.
        response.Line.Quantity.Should().Be(2m);
        response.Line.NetOfDiscount.Should().Be(360m);
        response.Line.VatAmount.Should().Be(54m);
        response.Line.LineTotal.Should().Be(414m);
    }

    [Fact]
    public async Task A_case_barcode_sells_its_pack_quantity_and_qualifies_for_the_wholesale_tier()
    {
        var pieceCode = "4015000010122";
        var caseCode = "CASE-" + Code("C");

        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 25m, barcode: pieceCode);

        await CatalogTestData.SetListPriceAsync(
            _fixture, _tenant.OrganizationId, _data.DefaultPriceListId, variantId, 20m);
        await CatalogTestData.SetListPriceAsync(
            _fixture, _tenant.OrganizationId, _data.DefaultPriceListId, variantId, 18m, minQuantity: 12m);

        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var addBarcode = scope.ServiceProvider
                .GetRequiredService<IUseCase<AddBarcodeRequest, ProductResponse>>();

            var added = await addBarcode.HandleAsync(new AddBarcodeRequest(
                variantId, caseCode, Domain.Catalog.BarcodeType.Internal, PackQuantity: 24m));

            added.IsSuccess.Should().BeTrue(added.IsFailure ? added.Error.ToString() : string.Empty);
        }

        await using var lookupScope = _fixture.CreateScope(
            _tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var lookup = lookupScope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var piece = await lookup.HandleAsync(new LookupProductRequest(pieceCode));
        piece.Value.Line.Quantity.Should().Be(1m);
        piece.Value.Price.UnitPrice.Should().Be(20m);

        var box = await lookup.HandleAsync(new LookupProductRequest(caseCode));
        box.Value.Product.PackQuantity.Should().Be(24m);
        box.Value.Line.Quantity.Should().Be(24m, "one scan of a case sells the whole case");
        box.Value.Price.UnitPrice.Should().Be(18m, "24 pieces qualifies for the tier that starts at 12");
        box.Value.Line.NetOfDiscount.Should().Be(432m);
    }

    [Fact]
    public async Task A_withdrawn_product_still_resolves_so_the_till_can_explain_itself()
    {
        var barcode = "96385074";
        var (productId, _) = await CatalogTestData.AddProductAsync(
            _fixture, _tenant.OrganizationId, _data, Code("P"), basePrice: 50m);

        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var addBarcode = scope.ServiceProvider
                .GetRequiredService<IUseCase<AddBarcodeRequest, ProductResponse>>();
            var product = await scope.ServiceProvider
                .GetRequiredService<IUseCase<GetProductRequest, ProductResponse>>()
                .HandleAsync(new GetProductRequest(productId));

            await addBarcode.HandleAsync(new AddBarcodeRequest(
                product.Value.Variants[0].VariantId, barcode, Domain.Catalog.BarcodeType.Ean8));

            var setStatus = scope.ServiceProvider
                .GetRequiredService<IUseCase<SetProductStatusRequest, ProductResponse>>();
            var archived = await setStatus.HandleAsync(
                new SetProductStatusRequest(productId, Domain.Catalog.CatalogStatus.Archived));
            archived.IsSuccess.Should().BeTrue();
        }

        await using var lookupScope = _fixture.CreateScope(
            _tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var lookup = lookupScope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var result = await lookup.HandleAsync(new LookupProductRequest(barcode));

        result.IsSuccess.Should().BeTrue("'unknown barcode' would send a cashier scanning four more times");
        result.Value.Product.IsSellable.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_code_is_a_clear_not_found()
    {
        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var lookup = scope.ServiceProvider
            .GetRequiredService<IUseCase<LookupProductRequest, LookupProductResponse>>();

        var result = await lookup.HandleAsync(new LookupProductRequest("NOPE-" + Code("X")));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("catalog.product_not_found");
    }

    [Fact]
    public async Task Inclusive_pricing_keeps_the_shelf_price_as_the_line_total()
    {
        // A second class configured tax-inclusive, as a shop displaying VAT-inclusive prices would.
        await using var setupScope = _fixture.CreateScope(
            _tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);

        var createClass = setupScope.ServiceProvider
            .GetRequiredService<IUseCase<CreateTaxClassRequest, TaxClassResponse>>();
        var created = await createClass.HandleAsync(new CreateTaxClassRequest(Code("INC"), "Inclusive VAT"));
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : string.Empty);

        var addVersion = setupScope.ServiceProvider
            .GetRequiredService<IUseCase<AddTaxRateVersionRequest, TaxClassResponse>>();
        var version = await addVersion.HandleAsync(new AddTaxRateVersionRequest(
            created.Value.TaxClassId,
            VatRate: 0.15m,
            SdRate: 0m,
            EffectiveFromUtc: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PriceIncludesTax: true));
        version.IsSuccess.Should().BeTrue(version.IsFailure ? version.Error.ToString() : string.Empty);

        var (_, variantId) = await CatalogTestData.AddProductAsync(
            _fixture,
            _tenant.OrganizationId,
            _data,
            Code("P"),
            basePrice: 115m,
            taxClassId: created.Value.TaxClassId);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var preview = scope.ServiceProvider.GetRequiredService<IUseCase<PreviewTaxRequest, PreviewTaxResponse>>();

        var result = await preview.HandleAsync(new PreviewTaxRequest(created.Value.TaxClassId, 115m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Treatment.Should().Be(TaxTreatment.Inclusive);
        result.Value.LineTotal.Should().Be(115m, "the shelf label is what the customer pays");
        result.Value.TaxableValue.Should().Be(100m);
        result.Value.VatAmount.Should().Be(15m);

        variantId.Should().NotBeEmpty();
    }

    private static async Task<string?> CatalogVariantSku(IServiceProvider services, Guid variantId)
    {
        var repository = services.GetRequiredService<Application.Catalog.Abstractions.ICatalogRepository>();
        var variant = await repository.FindVariantAsync(variantId);
        return variant?.Sku;
    }
}

using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Pricing;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>The reference data a catalog test needs before it can create a product.</summary>
/// <param name="PieceUomId">A whole-unit measure.</param>
/// <param name="KilogramUomId">A three-decimal weight measure.</param>
/// <param name="CategoryId">A category.</param>
/// <param name="BrandId">A brand.</param>
/// <param name="StandardTaxClassId">A 15% VAT class, tax-exclusive, in force since 2020.</param>
/// <param name="ExemptTaxClassId">An exempt class with a stated reason.</param>
/// <param name="DefaultPriceListId">The tenant's default price list.</param>
public sealed record CatalogFixtureData(
    Guid PieceUomId,
    Guid KilogramUomId,
    Guid CategoryId,
    Guid BrandId,
    Guid StandardTaxClassId,
    Guid ExemptTaxClassId,
    Guid DefaultPriceListId);

/// <summary>
/// Builds the catalog's reference data directly, so a test about pricing is about pricing rather
/// than about six set-up calls. The entities are created through their aggregates, so the data is
/// the same shape the API would have produced.
/// </summary>
internal static class CatalogTestData
{
    /// <summary>15%, the standard Bangladeshi VAT rate.</summary>
    public const decimal StandardVatRate = 0.15m;

    public static async Task<CatalogFixtureData> SeedReferenceDataAsync(
        DatabaseFixture fixture,
        Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var piece = UnitOfMeasure.Create(guids.NewId(), organizationId, "PCS", "Piece");
        var kilo = UnitOfMeasure.Create(guids.NewId(), organizationId, "KG", "Kilogram", UomKind.Weight, 3);
        context.UnitsOfMeasure.AddRange(piece, kilo);

        var category = Category.Create(guids.NewId(), organizationId, "GROCERY", "Grocery");
        context.Categories.Add(category);

        var brand = Brand.Create(guids.NewId(), organizationId, "ACME", "Acme Foods");
        context.Brands.Add(brand);

        var standard = TaxClass.Create(
            guids.NewId(), organizationId, "STD", "Standard VAT", TaxTreatment.Exclusive, isDefault: true);
        var exempt = TaxClass.Create(guids.NewId(), organizationId, "EXEMPT", "Exempt goods");
        exempt.SetExemption(TaxTreatment.Exempt, "SRO 100/2026 schedule 1");
        context.TaxClasses.AddRange(standard, exempt);

        // A rate in force well before any test's "now", so resolution finds it without the test
        // having to think about dates.
        var rule = TaxRule.Create(guids.NewId(), organizationId, standard.Id, "Standard VAT");
        rule.AddVersion(
            guids.NewId(),
            StandardVatRate,
            sdRate: 0m,
            sdInVatBase: true,
            priceIncludesTax: false,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "SRO-TEST/2020");
        context.TaxRules.Add(rule);

        var priceList = PriceList.Create(
            guids.NewId(), organizationId, "RETAIL", "Retail prices", priority: 0, isDefault: true);
        context.PriceLists.Add(priceList);

        await context.SaveChangesAsync().ConfigureAwait(false);

        return new CatalogFixtureData(
            piece.Id,
            kilo.Id,
            category.Id,
            brand.Id,
            standard.Id,
            exempt.Id,
            priceList.Id);
    }

    /// <summary>
    /// Creates an active product with one variant and, optionally, a barcode — the shape almost
    /// every test needs as a starting point.
    /// </summary>
    public static async Task<(Guid ProductId, Guid VariantId)> AddProductAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        CatalogFixtureData data,
        string code,
        decimal basePrice,
        string? barcode = null,
        Guid? taxClassId = null,
        bool trackBatch = false,
        bool trackExpiry = false)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(data);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var product = Product.Create(guids.NewId(), organizationId, code, code + " product", data.PieceUomId);
        product.Classify(data.CategoryId, data.BrandId, taxClassId ?? data.StandardTaxClassId);

        if (trackBatch || trackExpiry)
        {
            product.SetTrackingPolicy(true, trackBatch: true, trackSerial: false, trackExpiry: trackExpiry);
        }

        var variant = product.AddVariant(guids.NewId(), code, null, basePrice, data.PieceUomId);

        if (!string.IsNullOrWhiteSpace(barcode))
        {
            variant.AddBarcode(guids.NewId(), barcode, BarcodeType.Ean13);
        }

        product.Activate();

        context.Products.Add(product);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return (product.Id, variant.Id);
    }

    /// <summary>Sets a price in a list, creating the item or replacing the tier's price.</summary>
    public static async Task SetListPriceAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid priceListId,
        Guid variantId,
        decimal unitPrice,
        decimal minQuantity = 0m)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var list = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .Include(context.PriceLists, l => l.Items),
                l => l.Id == priceListId)
            .ConfigureAwait(false);

        list.SetPrice(guids.NewId(), variantId, unitPrice, minQuantity);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Creates a price list scoped to a customer group, with one price in it.</summary>
    public static async Task<Guid> AddGroupPriceListAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid customerGroupId,
        Guid variantId,
        decimal unitPrice,
        int priority = 10)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var list = PriceList.Create(
            guids.NewId(),
            organizationId,
            "GRP" + Random.Shared.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Group prices",
            priority,
            customerGroupId);

        list.SetPrice(guids.NewId(), variantId, unitPrice);
        context.PriceLists.Add(list);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return list.Id;
    }

    /// <summary>Sets a branch price override for a variant.</summary>
    public static async Task AddBranchPriceAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid branchId,
        Guid variantId,
        decimal unitPrice)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        context.BranchPriceOverrides.Add(BranchPriceOverride.Create(
            guids.NewId(), organizationId, branchId, variantId, unitPrice));

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a rate version to a tax class, for tests about effective dating and inclusive pricing.
    /// </summary>
    public static async Task AddTaxVersionAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid taxClassId,
        decimal vatRate,
        decimal sdRate,
        DateTimeOffset effectiveFromUtc,
        bool priceIncludesTax = false,
        bool sdInVatBase = true)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var rule = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .Include(context.TaxRules, r => r.Versions),
                r => r.TaxClassId == taxClassId && r.BranchId == null)
            .ConfigureAwait(false);

        rule.AddVersion(guids.NewId(), vatRate, sdRate, sdInVatBase, priceIncludesTax, effectiveFromUtc);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}

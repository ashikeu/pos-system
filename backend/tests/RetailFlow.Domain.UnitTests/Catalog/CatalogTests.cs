using FluentAssertions;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Platform;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Catalog;

/// <summary>
/// Catalog invariants (FR-CAT-01…08). The rules worth testing here are the ones that would produce
/// stock nobody can reconcile: tracking combinations, pack sizes, unit conversions and barcodes
/// that will never scan.
/// </summary>
public sealed class CatalogTests
{
    private static readonly Guid OrganizationId = Guid.Parse("0193f004-0000-7000-8000-000000000001");
    private static readonly Guid PieceUomId = Guid.Parse("0193f004-0000-7000-8000-000000000002");

    private static Product NewProduct(ProductType type = ProductType.Standard) =>
        Product.Create(Guid.NewGuid(), OrganizationId, "p-1001", "Rice 5kg", PieceUomId, type);

    [Fact]
    public void A_product_code_is_normalised_and_the_product_starts_as_a_draft()
    {
        var product = NewProduct();

        product.Code.Should().Be("P-1001");
        product.Status.Should().Be(CatalogStatus.Draft);
        product.TrackInventory.Should().BeTrue();
        product.IsSellable.Should().BeFalse();
    }

    [Fact]
    public void A_product_cannot_be_activated_without_a_variant()
    {
        var product = NewProduct();

        var activate = product.Activate;

        activate.Should().Throw<DomainException>("a product with no variant has no price and no barcode");
    }

    [Fact]
    public void The_first_variant_becomes_the_default()
    {
        var product = NewProduct();

        var first = product.AddVariant(Guid.NewGuid(), "sku-1", "5 kg", 450m, PieceUomId);
        var second = product.AddVariant(Guid.NewGuid(), "sku-2", "10 kg", 880m, PieceUomId);

        first.IsDefault.Should().BeTrue();
        second.IsDefault.Should().BeFalse();
        first.Sku.Should().Be("SKU-1");

        product.SetDefaultVariant(second.Id);
        first.IsDefault.Should().BeFalse();
        second.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Activating_needs_an_active_variant_and_then_the_product_is_sellable()
    {
        var product = NewProduct();
        product.AddVariant(Guid.NewGuid(), "sku-1", null, 100m, PieceUomId);

        product.Activate();

        product.Status.Should().Be(CatalogStatus.Active);
        product.IsSellable.Should().BeTrue();
    }

    [Fact]
    public void Archiving_a_product_archives_its_variants_and_freezes_it()
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 100m, PieceUomId);
        product.Activate();

        product.Archive();

        product.Status.Should().Be(CatalogStatus.Archived);
        variant.Status.Should().Be(CatalogStatus.Archived);

        var rename = () => product.UpdateDetails("New name", null, null);
        var reactivate = product.Activate;
        rename.Should().Throw<DomainException>();
        reactivate.Should().Throw<DomainException>();
    }

    [Fact]
    public void Expiry_tracking_requires_batch_tracking()
    {
        var product = NewProduct();

        var expiryWithoutBatch = () => product.SetTrackingPolicy(true, trackBatch: false, false, trackExpiry: true);

        expiryWithoutBatch.Should().Throw<DomainException>("an expiry date belongs to a batch");
    }

    [Fact]
    public void Serial_and_batch_tracking_cannot_both_be_on()
    {
        var product = NewProduct();

        var both = () => product.SetTrackingPolicy(true, trackBatch: true, trackSerial: true, false);

        both.Should().Throw<DomainException>();
    }

    [Fact]
    public void Tracking_without_inventory_tracking_is_refused()
    {
        var product = NewProduct();

        var orphan = () => product.SetTrackingPolicy(trackInventory: false, trackBatch: true, false, false);

        orphan.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_service_holds_no_stock_and_refuses_tracking()
    {
        var product = NewProduct(ProductType.Service);

        product.TrackInventory.Should().BeFalse();

        var track = () => product.SetTrackingPolicy(true, false, false, false);
        track.Should().Throw<DomainException>();

        product.SetTrackingPolicy(false, false, false, false);
        product.TrackInventory.Should().BeFalse();
    }

    [Fact]
    public void A_batch_tracked_product_with_expiry_keeps_its_near_expiry_window()
    {
        var product = NewProduct();

        product.SetTrackingPolicy(true, trackBatch: true, false, trackExpiry: true, nearExpiryDays: 30);

        product.TrackExpiry.Should().BeTrue();
        product.NearExpiryDays.Should().Be(30);
    }

    [Fact]
    public void Setting_the_same_attribute_twice_updates_it()
    {
        var product = NewProduct();

        product.SetAttribute(Guid.NewGuid(), "Origin", "Dinajpur");
        product.SetAttribute(Guid.NewGuid(), "origin", "Naogaon");

        product.Attributes.Should().ContainSingle().Which.Value.Should().Be("Naogaon");
    }

    [Fact]
    public void A_variant_rejects_a_negative_price_and_a_zero_pack_size()
    {
        var product = NewProduct();

        var negativePrice = () => product.AddVariant(Guid.NewGuid(), "sku-x", null, -1m, PieceUomId);
        var zeroPack = () => product.AddVariant(Guid.NewGuid(), "sku-y", null, 10m, PieceUomId, packSize: 0m);

        negativePrice.Should().Throw<DomainException>();
        zeroPack.Should().Throw<DomainException>();
    }

    [Fact]
    public void The_default_variant_cannot_be_withdrawn_while_it_is_the_default()
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 100m, PieceUomId);

        var deactivate = variant.Deactivate;

        deactivate.Should().Throw<DomainException>();
    }

    // ------------------------------------------------------------------ barcodes

    [Theory]
    [InlineData("4015000010122", BarcodeType.Ean13)]
    [InlineData("96385074", BarcodeType.Ean8)]
    [InlineData("036000291452", BarcodeType.UpcA)]
    public void A_valid_check_digit_is_accepted(string code, BarcodeType type)
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 100m, PieceUomId);

        var barcode = variant.AddBarcode(Guid.NewGuid(), code, type);

        barcode.Code.Should().Be(code);
        barcode.IsPrimary.Should().BeTrue("the first barcode on a variant is its primary one");
    }

    [Theory]
    [InlineData("4015000010123", BarcodeType.Ean13)]
    [InlineData("401500001012", BarcodeType.Ean13)]
    [InlineData("96385075", BarcodeType.Ean8)]
    [InlineData("03600029145X", BarcodeType.UpcA)]
    public void A_wrong_check_digit_or_length_is_refused(string code, BarcodeType type)
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 100m, PieceUomId);

        var add = () => variant.AddBarcode(Guid.NewGuid(), code, type);

        add.Should().Throw<DomainException>(
            "a mistyped barcode that is stored anyway will never scan, and the shop blames the scanner");
    }

    [Fact]
    public void An_internal_code_is_free_form_and_a_scanned_code_is_trimmed_and_upper_cased()
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 100m, PieceUomId);

        var barcode = variant.AddBarcode(Guid.NewGuid(), "  loose-rice-1  ", BarcodeType.Internal);

        barcode.Code.Should().Be("LOOSE-RICE-1");
    }

    [Fact]
    public void A_case_barcode_sells_its_pack_quantity_in_one_scan()
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 20m, PieceUomId);

        variant.AddBarcode(Guid.NewGuid(), "PIECE-1", BarcodeType.Internal);
        var caseCode = variant.AddBarcode(Guid.NewGuid(), "CASE-1", BarcodeType.Internal, packQuantity: 24m);

        caseCode.PackQuantity.Should().Be(24m);
        caseCode.IsPrimary.Should().BeFalse();

        variant.MakeBarcodePrimary(caseCode.Id);
        caseCode.IsPrimary.Should().BeTrue();
        variant.Barcodes.Count(b => b.IsPrimary).Should().Be(1);
    }

    [Fact]
    public void The_same_barcode_cannot_be_added_to_one_variant_twice()
    {
        var product = NewProduct();
        var variant = product.AddVariant(Guid.NewGuid(), "sku-1", null, 20m, PieceUomId);
        variant.AddBarcode(Guid.NewGuid(), "DUP-1", BarcodeType.Internal);

        var again = () => variant.AddBarcode(Guid.NewGuid(), "dup-1", BarcodeType.Internal);

        again.Should().Throw<DomainException>();
    }

    [Fact]
    public void The_check_digit_helper_matches_the_gs1_examples()
    {
        Barcode.ComputeCheckDigit("401500001012").Should().Be(2);
        Barcode.ComputeCheckDigit("9638507").Should().Be(4);
        Barcode.ComputeCheckDigit("03600029145").Should().Be(2);
    }

    // ------------------------------------------------------------------ units

    [Fact]
    public void A_unit_decides_whether_a_fractional_quantity_is_legal()
    {
        var piece = UnitOfMeasure.Create(Guid.NewGuid(), OrganizationId, "pcs", "Piece");
        var kilo = UnitOfMeasure.Create(Guid.NewGuid(), OrganizationId, "kg", "Kilogram", UomKind.Weight, 3);

        piece.AllowsQuantity(1.5m).Should().BeFalse("half a bottle is not a thing a till can sell");
        piece.Normalize(1.5m).Should().Be(2m);
        kilo.AllowsQuantity(1.5m).Should().BeTrue();
        kilo.AllowsQuantity(1.5005m).Should().BeFalse();
    }

    [Fact]
    public void A_unit_cannot_carry_more_decimals_than_the_platform_quantity_scale()
    {
        var tooPrecise = () => UnitOfMeasure.Create(
            Guid.NewGuid(), OrganizationId, "mg", "Milligram", UomKind.Weight, decimalPlaces: 6);

        tooPrecise.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_conversion_only_works_inside_one_unit_family()
    {
        var kilo = UnitOfMeasure.Create(Guid.NewGuid(), OrganizationId, "kg", "Kilogram", UomKind.Weight, 3);
        var gram = UnitOfMeasure.Create(Guid.NewGuid(), OrganizationId, "g", "Gram", UomKind.Weight, 0);
        var piece = UnitOfMeasure.Create(Guid.NewGuid(), OrganizationId, "pcs", "Piece");

        var conversion = UomConversion.Create(Guid.NewGuid(), OrganizationId, kilo, gram, 1_000m);
        conversion.ToBase(2.5m).Should().Be(2_500m);
        conversion.FromBase(500m).Should().Be(0.5m);

        var crossFamily = () => UomConversion.Create(Guid.NewGuid(), OrganizationId, kilo, piece, 1m);
        crossFamily.Should().Throw<DomainException>();

        var toItself = () => UomConversion.Create(Guid.NewGuid(), OrganizationId, kilo, kilo, 1m);
        toItself.Should().Throw<DomainException>();

        var zeroFactor = () => UomConversion.Create(Guid.NewGuid(), OrganizationId, kilo, gram, 0m);
        zeroFactor.Should().Throw<DomainException>();
    }

    // ------------------------------------------------------------------ categories and branch settings

    [Fact]
    public void Categories_nest_to_a_bounded_depth()
    {
        var root = Category.Create(Guid.NewGuid(), OrganizationId, "food", "Food");
        var level2 = Category.Create(Guid.NewGuid(), OrganizationId, "grain", "Grains", root);
        var level3 = Category.Create(Guid.NewGuid(), OrganizationId, "rice", "Rice", level2);
        var level4 = Category.Create(Guid.NewGuid(), OrganizationId, "aromatic", "Aromatic", level3);

        level4.Depth.Should().Be(Category.MaxDepth);

        var tooDeep = () => Category.Create(Guid.NewGuid(), OrganizationId, "kalijira", "Kalijira", level4);
        tooDeep.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_category_cannot_become_its_own_parent()
    {
        var category = Category.Create(Guid.NewGuid(), OrganizationId, "food", "Food");

        var cycle = () => category.MoveUnder(category);

        cycle.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_reorder_level_below_the_minimum_stock_is_refused()
    {
        var setting = ProductBranchSetting.Create(
            Guid.NewGuid(), OrganizationId, Guid.NewGuid(), Guid.NewGuid());

        var tooLate = () => setting.SetThresholds(minStock: 20m, reorderLevel: 10m, reorderQuantity: 50m);
        tooLate.Should().Throw<DomainException>("the alert would fire after the shelf is already empty");

        setting.SetThresholds(minStock: 10m, reorderLevel: 20m, reorderQuantity: 50m);
        setting.MinStock.Should().Be(10m);
        setting.ReorderLevel.Should().Be(20m);
        setting.IsSellable.Should().BeTrue();
    }
}

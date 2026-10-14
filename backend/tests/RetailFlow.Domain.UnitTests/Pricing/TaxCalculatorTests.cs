using FluentAssertions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Pricing;

/// <summary>
/// The tax engine against the formulas in <c>docs/02-business-rules.md</c> §2. These are the most
/// important tests in the repository: every number on every receipt and every VAT return comes
/// through here, and an error would be systematic rather than occasional.
/// </summary>
public sealed class TaxCalculatorTests
{
    private const decimal Vat15 = 0.15m;
    private const decimal Sd10 = 0.10m;

    private static TaxSnapshot Exclusive(decimal vat = Vat15, decimal sd = 0m, bool sdInVatBase = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), TaxTreatment.Exclusive, vat, sd, sdInVatBase);

    private static TaxSnapshot Inclusive(decimal vat = Vat15, decimal sd = 0m, bool sdInVatBase = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), TaxTreatment.Inclusive, vat, sd, sdInVatBase);

    [Fact]
    public void Exclusive_vat_is_added_to_the_line()
    {
        var result = TaxCalculator.Compute(Exclusive(), quantity: 2m, unitPrice: 100m);

        result.Gross.Should().Be(200m);
        result.NetOfDiscount.Should().Be(200m);
        result.SdAmount.Should().Be(0m);
        result.TaxableValue.Should().Be(200m);
        result.VatAmount.Should().Be(30m);
        result.LineTotal.Should().Be(230m);
        result.NetValue.Should().Be(200m);
    }

    [Fact]
    public void Exclusive_sd_is_charged_before_vat_and_sits_in_the_vat_base()
    {
        // net 1000, SD 10% = 100, VAT base 1100, VAT 15% = 165, total 1265.
        var result = TaxCalculator.Compute(Exclusive(sd: Sd10), quantity: 10m, unitPrice: 100m);

        result.NetOfDiscount.Should().Be(1_000m);
        result.SdAmount.Should().Be(100m);
        result.TaxableValue.Should().Be(1_100m);
        result.VatAmount.Should().Be(165m);
        result.LineTotal.Should().Be(1_265m);
        result.TotalTax.Should().Be(265m);
    }

    [Fact]
    public void Exclusive_sd_outside_the_vat_base_charges_vat_on_the_net_only()
    {
        var result = TaxCalculator.Compute(
            Exclusive(sd: Sd10, sdInVatBase: false), quantity: 10m, unitPrice: 100m);

        result.SdAmount.Should().Be(100m);
        result.VatAmount.Should().Be(150m, "VAT is charged on 1 000, not on 1 100");
        result.LineTotal.Should().Be(1_250m);
    }

    [Fact]
    public void Inclusive_pricing_leaves_the_line_total_exactly_as_displayed()
    {
        // A 115 BDT shelf price at 15% VAT decomposes to 100 + 15.
        var result = TaxCalculator.Compute(Inclusive(), quantity: 1m, unitPrice: 115m);

        result.LineTotal.Should().Be(115m, "a tax-inclusive price must mean what the label says");
        result.TaxableValue.Should().Be(100m);
        result.VatAmount.Should().Be(15m);
        result.NetValue.Should().Be(100m);
    }

    [Fact]
    public void Inclusive_pricing_with_sd_uses_the_compound_divisor()
    {
        // divisor = 1 + 0.10 + 0.15 + 0.015 = 1.265; 1265 / 1.265 = 1000 base, SD 100, VAT 165.
        var result = TaxCalculator.Compute(Inclusive(sd: Sd10), quantity: 1m, unitPrice: 1_265m);

        result.TaxableValue.Should().Be(1_100m);
        result.SdAmount.Should().Be(100m);
        result.VatAmount.Should().Be(165m);
        result.LineTotal.Should().Be(1_265m);
    }

    [Fact]
    public void Inclusive_pricing_without_sd_in_the_base_uses_the_simple_divisor()
    {
        // divisor = 1 + 0.10 + 0.15 = 1.25; 1250 / 1.25 = 1000 base, SD 100, VAT 150.
        var result = TaxCalculator.Compute(
            Inclusive(sd: Sd10, sdInVatBase: false), quantity: 1m, unitPrice: 1_250m);

        result.TaxableValue.Should().Be(1_100m);
        result.SdAmount.Should().Be(100m);
        result.VatAmount.Should().Be(150m);
        result.LineTotal.Should().Be(1_250m);
    }

    [Theory]
    [InlineData(99.99)]
    [InlineData(10.01)]
    [InlineData(7.77)]
    [InlineData(1.0)]
    [InlineData(333.33)]
    public void Inclusive_components_always_add_back_up_to_the_price(decimal unitPrice)
    {
        var result = TaxCalculator.Compute(Inclusive(sd: Sd10), quantity: 3m, unitPrice: unitPrice);

        (result.TaxableValue + result.VatAmount).Should().Be(result.LineTotal,
            "the decomposition is what the customer pays, so a rounding residual cannot be left behind");
        result.NetValue.Should().Be(result.TaxableValue - result.SdAmount);
    }

    [Fact]
    public void A_line_discount_reduces_the_taxable_value_not_just_the_total()
    {
        // 1000 gross, 100 discount → 900 net, VAT 135.
        var result = TaxCalculator.Compute(Exclusive(), quantity: 10m, unitPrice: 100m, lineDiscount: 100m);

        result.NetOfDiscount.Should().Be(900m);
        result.VatAmount.Should().Be(135m);
        result.LineTotal.Should().Be(1_035m);
    }

    [Fact]
    public void A_bill_discount_share_reduces_the_line_the_same_way()
    {
        var result = TaxCalculator.Compute(
            Exclusive(), quantity: 10m, unitPrice: 100m, lineDiscount: 50m, billDiscount: 50m);

        result.LineDiscount.Should().Be(50m);
        result.BillDiscount.Should().Be(50m);
        result.NetOfDiscount.Should().Be(900m);
        result.VatAmount.Should().Be(135m);
    }

    [Fact]
    public void A_discount_larger_than_the_line_is_refused_rather_than_making_a_negative_line()
    {
        var compute = () => TaxCalculator.Compute(Exclusive(), quantity: 1m, unitPrice: 100m, lineDiscount: 150m);

        compute.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_full_discount_produces_a_zero_line_with_no_tax()
    {
        var result = TaxCalculator.Compute(Exclusive(), quantity: 1m, unitPrice: 100m, lineDiscount: 100m);

        result.NetOfDiscount.Should().Be(0m);
        result.VatAmount.Should().Be(0m);
        result.LineTotal.Should().Be(0m);
    }

    [Theory]
    [InlineData(TaxTreatment.Exempt)]
    [InlineData(TaxTreatment.ZeroRated)]
    public void Exempt_and_zero_rated_lines_carry_no_tax_but_keep_their_reason(TaxTreatment treatment)
    {
        var snapshot = new TaxSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), treatment, Vat15, Sd10, SdInVatBase: true, "SRO 123/2026");

        var result = TaxCalculator.Compute(snapshot, quantity: 4m, unitPrice: 25m);

        result.VatAmount.Should().Be(0m, "the rates on the snapshot are ignored for these treatments");
        result.SdAmount.Should().Be(0m);
        result.LineTotal.Should().Be(100m);
        result.Tax.ExemptionReason.Should().Be("SRO 123/2026");
    }

    [Fact]
    public void A_product_with_no_tax_class_is_treated_as_exempt_and_says_so()
    {
        var result = TaxCalculator.Compute(TaxSnapshot.None, quantity: 1m, unitPrice: 50m);

        result.VatAmount.Should().Be(0m);
        result.Tax.ExemptionReason.Should().Be("No tax class configured");
        result.Tax.IsTaxable.Should().BeFalse();
    }

    [Fact]
    public void Intermediate_amounts_keep_four_decimals()
    {
        // 3 × 33.3333 = 99.9999 exactly; rounding to two here would lose a sub-unit per line.
        var result = TaxCalculator.Compute(Exclusive(), quantity: 3m, unitPrice: 33.3333m);

        result.Gross.Should().Be(99.9999m);
        result.VatAmount.Should().Be(15.0000m);
        result.LineTotal.Should().Be(114.9999m);
    }

    [Fact]
    public void A_zero_or_negative_quantity_is_rejected()
    {
        var zero = () => TaxCalculator.Compute(Exclusive(), quantity: 0m, unitPrice: 10m);
        var negative = () => TaxCalculator.Compute(Exclusive(), quantity: -1m, unitPrice: 10m);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_bill_discount_is_allocated_so_the_parts_sum_to_the_discount_exactly()
    {
        var lines = new[] { 33.33m, 33.33m, 33.34m };

        var allocated = TaxCalculator.AllocateBillDiscount(10m, lines);

        allocated.Sum().Should().Be(10m, "largest-remainder allocation never loses or invents a sub-unit");
        allocated.Should().OnlyContain(amount => amount > 0m);
    }

    [Fact]
    public void A_bill_discount_bigger_than_the_lines_is_refused()
    {
        var allocate = () => TaxCalculator.AllocateBillDiscount(100m, new[] { 10m, 20m });

        allocate.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_bill_discount_over_zero_value_lines_allocates_nothing()
    {
        var allocated = TaxCalculator.AllocateBillDiscount(0m, new[] { 0m, 0m });

        allocated.Should().HaveCount(2).And.OnlyContain(amount => amount == 0m);
    }

    [Fact]
    public void A_full_worked_bill_matches_the_documented_totals()
    {
        // Two lines, tax-exclusive, 15% VAT, one with 10% SD, plus a 100 BDT bill discount spread
        // in proportion to line value. Checks §2 and §3 together.
        var lineValues = new[] { 1_000m, 500m };
        var allocation = TaxCalculator.AllocateBillDiscount(100m, lineValues);

        allocation[0].Should().Be(66.6667m);
        allocation[1].Should().Be(33.3333m);

        var first = TaxCalculator.Compute(
            Exclusive(sd: Sd10), quantity: 10m, unitPrice: 100m, billDiscount: allocation[0]);
        var second = TaxCalculator.Compute(
            Exclusive(), quantity: 5m, unitPrice: 100m, billDiscount: allocation[1]);

        var taxableValue = first.TaxableValue + second.TaxableValue;
        var totalVat = first.VatAmount + second.VatAmount;
        var totalSd = first.SdAmount + second.SdAmount;

        totalSd.Should().Be(93.3333m, "SD is charged on the discounted value, not on the list value");
        taxableValue.Should().Be(1_493.3333m);
        totalVat.Should().Be(224.0000m);

        var grandTotal = RoundingPolicy.Round2(taxableValue + totalVat);
        grandTotal.Should().Be(1_717.33m);

        // The bill is rounded to two places exactly once; the lines keep four (ADR-0006).
        (first.LineTotal + second.LineTotal).Should().Be(1_717.3333m);
    }
}

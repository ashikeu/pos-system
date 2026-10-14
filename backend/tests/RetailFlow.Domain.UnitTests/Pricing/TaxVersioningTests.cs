using FluentAssertions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Pricing;

/// <summary>
/// Effective-dated, versioned tax rules (ADR-0019). The point of the versions is that a rate change
/// must not alter what last month's sales were taxed at, so the timeline is tested directly.
/// </summary>
public sealed class TaxVersioningTests
{
    private static readonly Guid OrganizationId = Guid.Parse("0193f003-0000-7000-8000-000000000001");
    private static readonly Guid TaxClassId = Guid.Parse("0193f003-0000-7000-8000-000000000002");

    private static DateTimeOffset At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static TaxRule NewRule() =>
        TaxRule.Create(Guid.NewGuid(), OrganizationId, TaxClassId, "Standard VAT");

    [Fact]
    public void Adding_a_version_closes_the_previous_one_so_the_timeline_has_no_gaps()
    {
        var rule = NewRule();

        var first = rule.AddVersion(Guid.NewGuid(), 0.15m, 0m, true, false, At(2026, 1, 1));
        var second = rule.AddVersion(Guid.NewGuid(), 0.175m, 0m, true, false, At(2026, 7, 1));

        first.EffectiveToUtc.Should().Be(At(2026, 7, 1));
        first.IsCurrent.Should().BeFalse();
        second.IsCurrent.Should().BeTrue();
        second.Version.Should().Be(2);

        rule.CurrentVersion(At(2026, 3, 1)).Should().Be(first);
        rule.CurrentVersion(At(2026, 7, 1)).Should().Be(second, "the new rate applies from its first instant");
        rule.CurrentVersion(At(2026, 12, 1)).Should().Be(second);
    }

    [Fact]
    public void Before_the_first_version_starts_there_is_no_rate()
    {
        var rule = NewRule();
        rule.AddVersion(Guid.NewGuid(), 0.15m, 0m, true, false, At(2026, 6, 1));

        rule.CurrentVersion(At(2026, 1, 1)).Should().BeNull(
            "charging a rate that was not yet in force would be worse than refusing to price");
    }

    [Fact]
    public void A_backdated_version_is_refused()
    {
        var rule = NewRule();
        rule.AddVersion(Guid.NewGuid(), 0.15m, 0m, true, false, At(2026, 6, 1));

        var backdate = () => rule.AddVersion(Guid.NewGuid(), 0.10m, 0m, true, false, At(2026, 1, 1));

        backdate.Should().Throw<DomainException>(
            "it would change what already-filed sales were taxed at");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void A_rate_outside_zero_to_one_is_refused(decimal rate)
    {
        var rule = NewRule();

        var add = () => rule.AddVersion(Guid.NewGuid(), rate, 0m, true, false, At(2026, 1, 1));

        add.Should().Throw<DomainException>("15% is 0.15, and 15 would multiply every bill by sixteen");
    }

    [Fact]
    public void A_version_keeps_the_sro_reference_for_the_audit_file()
    {
        var rule = NewRule();

        var version = rule.AddVersion(
            Guid.NewGuid(), 0.15m, 0.10m, true, true, At(2026, 1, 1), "SRO-204/2026", "Budget change");

        version.SroReference.Should().Be("SRO-204/2026");
        version.Notes.Should().Be("Budget change");
        version.SdInVatBase.Should().BeTrue();
        version.PriceIncludesTax.Should().BeTrue();
    }

    [Fact]
    public void An_exempt_class_needs_a_reason_because_the_vat_return_distinguishes_them()
    {
        var taxClass = TaxClass.Create(Guid.NewGuid(), OrganizationId, "EXEMPT", "Exempt goods");

        var withoutReason = () => taxClass.SetExemption(TaxTreatment.Exempt, null);
        withoutReason.Should().Throw<DomainException>();

        taxClass.SetExemption(TaxTreatment.ZeroRated, "SRO 100/2026 schedule 1");
        taxClass.Treatment.Should().Be(TaxTreatment.ZeroRated);
        taxClass.ExemptionReason.Should().Be("SRO 100/2026 schedule 1");
    }

    [Fact]
    public void The_default_tax_class_cannot_be_deactivated_while_it_is_the_default()
    {
        var taxClass = TaxClass.Create(
            Guid.NewGuid(), OrganizationId, "STD", "Standard", TaxTreatment.Exclusive, isDefault: true);

        var deactivate = taxClass.Deactivate;
        deactivate.Should().Throw<DomainException>();

        taxClass.ClearDefault();
        taxClass.Deactivate();
        taxClass.IsActive.Should().BeFalse();
    }

    [Fact]
    public void A_scoped_price_list_cannot_be_the_tenant_default()
    {
        var create = () => PriceList.Create(
            Guid.NewGuid(), OrganizationId, "GRP", "Group list", 0, customerGroupId: Guid.NewGuid(), isDefault: true);

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_price_list_item_is_replaced_per_quantity_tier_rather_than_duplicated()
    {
        var list = PriceList.Create(Guid.NewGuid(), OrganizationId, "RETAIL", "Retail", isDefault: true);
        var variantId = Guid.NewGuid();

        list.SetPrice(Guid.NewGuid(), variantId, 20m);
        list.SetPrice(Guid.NewGuid(), variantId, 19m);
        list.SetPrice(Guid.NewGuid(), variantId, 18m, minQuantity: 12m);

        list.Items.Should().HaveCount(2, "one row per tier, and setting the same tier updates it");
        list.Items.Single(i => i.MinQuantity == 0m).UnitPrice.Should().Be(19m);
        list.Items.Single(i => i.MinQuantity == 12m).UnitPrice.Should().Be(18m);
    }

    [Fact]
    public void An_effective_window_that_ends_before_it_starts_is_refused()
    {
        var list = PriceList.Create(Guid.NewGuid(), OrganizationId, "SALE", "Eid sale");

        var backwards = () => list.SetEffectiveWindow(At(2026, 6, 1), At(2026, 5, 1));

        backwards.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_price_list_is_effective_only_inside_its_window()
    {
        var list = PriceList.Create(Guid.NewGuid(), OrganizationId, "SALE", "Eid sale");
        list.SetEffectiveWindow(At(2026, 3, 1), At(2026, 4, 1));

        list.IsEffectiveAt(At(2026, 2, 28)).Should().BeFalse();
        list.IsEffectiveAt(At(2026, 3, 1)).Should().BeTrue();
        list.IsEffectiveAt(At(2026, 3, 31)).Should().BeTrue();
        list.IsEffectiveAt(At(2026, 4, 1)).Should().BeFalse("the end is exclusive");
    }
}

using FluentAssertions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Pricing;

/// <summary>
/// The price resolution order from <c>docs/02-business-rules.md</c> §1. A shop notices a wrong
/// price immediately and trusts the system less for the rest of the day, so the precedence is
/// pinned down case by case.
/// </summary>
public sealed class PriceResolutionTests
{
    private static readonly Guid GroupListId = Guid.Parse("0193f002-0000-7000-8000-000000000001");
    private static readonly Guid DefaultListId = Guid.Parse("0193f002-0000-7000-8000-000000000002");
    private static readonly Guid BranchOverrideId = Guid.Parse("0193f002-0000-7000-8000-000000000003");

    [Fact]
    public void With_no_candidates_the_variant_base_price_wins()
    {
        var resolved = PriceResolution.Select([], basePrice: 49.5m);

        resolved.UnitPrice.Should().Be(49.5m);
        resolved.Source.Should().Be(PriceSource.VariantBasePrice);
        resolved.IsOverridden.Should().BeFalse();
    }

    [Fact]
    public void The_full_order_is_manual_then_customer_then_group_then_branch_then_default()
    {
        var candidates = new[]
        {
            new PriceCandidate(PriceSource.DefaultPriceList, 100m, DefaultListId),
            new PriceCandidate(PriceSource.BranchPriceOverride, 95m, BranchOverrideId),
            new PriceCandidate(PriceSource.CustomerGroupPriceList, 90m, GroupListId),
            new PriceCandidate(PriceSource.CustomerPrice, 85m),
            new PriceCandidate(PriceSource.ManualOverride, 80m),
        };

        PriceResolution.Select(candidates, 120m).Source.Should().Be(PriceSource.ManualOverride);
        PriceResolution.Select(candidates.Skip(4).Take(0).Concat(candidates[..4]), 120m)
            .Source.Should().Be(PriceSource.CustomerPrice);
        PriceResolution.Select(candidates[..3], 120m).Source.Should().Be(PriceSource.CustomerGroupPriceList);
        PriceResolution.Select(candidates[..2], 120m).Source.Should().Be(PriceSource.BranchPriceOverride);
        PriceResolution.Select(candidates[..1], 120m).Source.Should().Be(PriceSource.DefaultPriceList);
    }

    [Fact]
    public void A_cheaper_default_list_does_not_beat_a_dearer_branch_override()
    {
        // Precedence is not "cheapest wins": a branch that prices higher does so on purpose.
        var candidates = new[]
        {
            new PriceCandidate(PriceSource.DefaultPriceList, 80m, DefaultListId),
            new PriceCandidate(PriceSource.BranchPriceOverride, 120m, BranchOverrideId),
        };

        var resolved = PriceResolution.Select(candidates, basePrice: 100m);

        resolved.UnitPrice.Should().Be(120m);
        resolved.Source.Should().Be(PriceSource.BranchPriceOverride);
        resolved.SourceId.Should().Be(BranchOverrideId);
    }

    [Fact]
    public void Within_one_source_the_higher_priority_list_wins()
    {
        var candidates = new[]
        {
            new PriceCandidate(PriceSource.CustomerGroupPriceList, 100m, GroupListId, Priority: 1),
            new PriceCandidate(PriceSource.CustomerGroupPriceList, 90m, DefaultListId, Priority: 5),
        };

        PriceResolution.Select(candidates, 120m).SourceId.Should().Be(DefaultListId);
    }

    [Fact]
    public void A_quantity_tier_applies_only_from_its_minimum_quantity()
    {
        var candidates = new[]
        {
            new PriceCandidate(PriceSource.DefaultPriceList, 20m, DefaultListId),
            new PriceCandidate(PriceSource.DefaultPriceList, 18m, DefaultListId, MinQuantity: 12m),
        };

        PriceResolution.Select(candidates, basePrice: 25m, quantity: 6m).UnitPrice.Should().Be(20m);
        PriceResolution.Select(candidates, basePrice: 25m, quantity: 12m).UnitPrice.Should().Be(18m);
        PriceResolution.Select(candidates, basePrice: 25m, quantity: 100m).UnitPrice.Should().Be(18m);
    }

    [Fact]
    public void The_highest_qualifying_tier_wins_when_several_qualify()
    {
        var candidates = new[]
        {
            new PriceCandidate(PriceSource.DefaultPriceList, 20m, DefaultListId),
            new PriceCandidate(PriceSource.DefaultPriceList, 18m, DefaultListId, MinQuantity: 12m),
            new PriceCandidate(PriceSource.DefaultPriceList, 16m, DefaultListId, MinQuantity: 48m),
        };

        var resolved = PriceResolution.Select(candidates, basePrice: 25m, quantity: 50m);

        resolved.UnitPrice.Should().Be(16m);
        resolved.MinQuantity.Should().Be(48m);
    }

    [Fact]
    public void A_zero_price_candidate_is_honoured_rather_than_treated_as_missing()
    {
        // A free line is a real business case (gift, sample) and must not silently fall through to
        // the base price.
        var candidates = new[] { new PriceCandidate(PriceSource.ManualOverride, 0m) };

        var resolved = PriceResolution.Select(candidates, basePrice: 100m);

        resolved.UnitPrice.Should().Be(0m);
        resolved.Source.Should().Be(PriceSource.ManualOverride);
    }

    [Fact]
    public void A_negative_candidate_is_ignored_and_a_negative_base_price_is_refused()
    {
        var candidates = new[] { new PriceCandidate(PriceSource.DefaultPriceList, -5m, DefaultListId) };

        PriceResolution.Select(candidates, basePrice: 10m).Source.Should().Be(PriceSource.VariantBasePrice);

        var negativeBase = () => PriceResolution.Select([], basePrice: -1m);
        negativeBase.Should().Throw<DomainException>();
    }

    [Fact]
    public void Resolution_is_deterministic_when_two_candidates_tie_on_everything()
    {
        var first = new PriceCandidate(PriceSource.DefaultPriceList, 90m, DefaultListId);
        var second = new PriceCandidate(PriceSource.DefaultPriceList, 95m, GroupListId);

        // Same answer whichever order the repository happened to return them in: every till and
        // every sync must resolve identically.
        PriceResolution.Select([first, second], 100m).UnitPrice.Should().Be(90m);
        PriceResolution.Select([second, first], 100m).UnitPrice.Should().Be(90m);
    }

    [Fact]
    public void A_discount_rule_caps_its_percentage_in_money_and_never_exceeds_the_line()
    {
        var rule = DiscountRule.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "WEEK10",
            "10% off this week",
            DiscountScope.Category,
            DiscountKind.Percent,
            10m,
            targetId: Guid.NewGuid());

        rule.SetConditions(null, null, null, null, maxDiscountAmount: 50m);

        rule.AmountFor(1_000m).Should().Be(50m, "the money cap bites before the percentage does");
        rule.AmountFor(200m).Should().Be(20m);
        rule.AmountFor(0m).Should().Be(0m);
    }

    [Fact]
    public void A_fixed_discount_never_exceeds_the_line_value()
    {
        var rule = DiscountRule.Create(
            Guid.NewGuid(), Guid.NewGuid(), "OFF50", "50 off", DiscountScope.Bill, DiscountKind.Amount, 50m);

        rule.AmountFor(30m).Should().Be(30m, "a discount cannot turn a line into a refund");
        rule.AmountFor(500m).Should().Be(50m);
    }

    [Fact]
    public void A_percentage_above_one_hundred_and_a_scope_without_a_target_are_refused()
    {
        var tooMuch = () => DiscountRule.Create(
            Guid.NewGuid(), Guid.NewGuid(), "BAD", "Bad", DiscountScope.Bill, DiscountKind.Percent, 150m);

        var noTarget = () => DiscountRule.Create(
            Guid.NewGuid(), Guid.NewGuid(), "BAD", "Bad", DiscountScope.Variant, DiscountKind.Percent, 10m);

        tooMuch.Should().Throw<DomainException>();
        noTarget.Should().Throw<DomainException>();
    }
}

using FluentAssertions;
using RetailFlow.Domain.Platform;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Platform;

public class MoneyTests
{
    [Fact]
    public void From_defaults_to_BDT()
    {
        var money = Money.From(150.50m);

        money.CurrencyCode.Should().Be("BDT");
        money.Amount.Should().Be(150.50m);
    }

    [Fact]
    public void From_keeps_four_decimals_for_intermediate_maths()
    {
        Money.From(12.34567m).Amount.Should().Be(12.3457m);
    }

    [Fact]
    public void Settled_rounds_to_two_decimals()
    {
        Money.Settled(12.345m).Amount.Should().Be(12.35m);
    }

    [Fact]
    public void Arithmetic_across_currencies_throws_instead_of_guessing()
    {
        var bdt = Money.From(100m);
        var usd = Money.From(100m, "USD");

        var act = () => bdt + usd;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*different currencies*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("BD")]
    [InlineData("TAKA")]
    public void Invalid_currency_codes_are_rejected(string code)
    {
        var act = () => Money.From(1m, code);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Currency_codes_are_normalised_to_upper_case()
    {
        Money.From(1m, "bdt").CurrencyCode.Should().Be("BDT");
    }

    [Fact]
    public void Addition_and_subtraction_behave_as_expected()
    {
        var total = Money.From(100m) + Money.From(25.25m) - Money.From(0.25m);

        total.Amount.Should().Be(125.00m);
    }

    [Fact]
    public void Multiplication_rounds_at_four_decimals()
    {
        (Money.From(10.555m) * 3m).Amount.Should().Be(31.665m);
    }

    [Fact]
    public void Division_by_zero_throws()
    {
        var act = () => Money.From(10m).Divide(0m);

        act.Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void Allocate_splits_without_losing_sub_units()
    {
        var parts = Money.From(10m).Allocate([1m, 1m, 1m]);

        parts.Sum(p => p.Amount).Should().Be(10m);
        parts.Should().OnlyContain(p => p.CurrencyCode == "BDT");
    }

    [Fact]
    public void Comparison_operators_compare_amounts_of_the_same_currency()
    {
        (Money.From(10m) > Money.From(9.99m)).Should().BeTrue();
        (Money.From(10m) <= Money.From(10m)).Should().BeTrue();
    }

    [Fact]
    public void Equality_considers_currency()
    {
        Money.From(10m).Should().Be(Money.From(10m));
        Money.From(10m).Equals(Money.From(10m, "USD")).Should().BeFalse();
    }

    [Fact]
    public void ToString_is_culture_independent()
    {
        Money.From(1234.5m).ToString().Should().Be("1234.50 BDT");
    }
}

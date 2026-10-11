using FluentAssertions;
using RetailFlow.Domain.Platform;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Platform;

public class RoundingPolicyTests
{
    [Theory]
    [InlineData(2.345, 2.35)]   // half away from zero, not to even
    [InlineData(2.355, 2.36)]
    [InlineData(-2.345, -2.35)]
    [InlineData(10.004, 10.00)]
    public void Round2_rounds_half_away_from_zero(decimal input, decimal expected) =>
        RoundingPolicy.Round2(input).Should().Be(expected);

    [Theory]
    [InlineData(123.45, 1.0, CashRoundingMode.Nearest, 123.00)]
    [InlineData(123.55, 1.0, CashRoundingMode.Nearest, 124.00)]
    [InlineData(123.10, 1.0, CashRoundingMode.Up, 124.00)]
    [InlineData(123.90, 1.0, CashRoundingMode.Down, 123.00)]
    [InlineData(123.45, 0.0, CashRoundingMode.Nearest, 123.45)]  // increment 0 disables rounding
    [InlineData(127.00, 5.0, CashRoundingMode.Nearest, 125.00)]
    public void RoundToIncrement_applies_the_configured_cash_increment(
        decimal amount, decimal increment, CashRoundingMode mode, decimal expected) =>
        RoundingPolicy.RoundToIncrement(amount, increment, mode).Should().Be(expected);

    [Fact]
    public void Allocate_distributes_the_exact_total_when_the_split_is_not_clean()
    {
        // 10.00 across three equal lines cannot be split evenly at 2 dp.
        var parts = RoundingPolicy.Allocate(10.00m, [1m, 1m, 1m]);

        parts.Sum().Should().Be(10.00m);
        parts.Should().OnlyContain(p => p == 3.33m || p == 3.34m);
        parts.Count(p => p == 3.34m).Should().Be(1);
    }

    [Fact]
    public void Allocate_weights_parts_by_line_value()
    {
        var parts = RoundingPolicy.Allocate(100m, [300m, 100m]);

        parts.Should().BeEquivalentTo(new[] { 75.00m, 25.00m }, o => o.WithStrictOrdering());
        parts.Sum().Should().Be(100m);
    }

    [Fact]
    public void Allocate_gives_the_residual_to_the_largest_remainders_first()
    {
        // 0.05 over four lines with uneven weights: residual sub-units must land on the lines
        // with the biggest fractional parts, never all on line one.
        var parts = RoundingPolicy.Allocate(0.05m, [1m, 1m, 1m, 1m]);

        parts.Sum().Should().Be(0.05m);
        parts.Count(p => p == 0.02m).Should().Be(1);
        parts.Count(p => p == 0.01m).Should().Be(3);
    }

    [Fact]
    public void Allocate_handles_negative_totals_such_as_returns()
    {
        var parts = RoundingPolicy.Allocate(-10.00m, [1m, 1m, 1m]);

        parts.Sum().Should().Be(-10.00m);
        parts.Should().OnlyContain(p => p < 0m);
    }

    [Fact]
    public void Allocate_puts_the_whole_amount_on_the_first_line_when_all_weights_are_zero()
    {
        var parts = RoundingPolicy.Allocate(5.00m, [0m, 0m]);

        parts.Sum().Should().Be(5.00m);
        parts[0].Should().Be(5.00m);
    }

    [Fact]
    public void Allocate_rejects_negative_weights()
    {
        var act = () => RoundingPolicy.Allocate(10m, [1m, -1m]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocate_returns_an_empty_result_for_no_lines() =>
        RoundingPolicy.Allocate(10m, []).Should().BeEmpty();

    [Fact]
    public void Allocate_at_four_decimals_still_balances()
    {
        var parts = RoundingPolicy.Allocate(1.0001m, [1m, 1m, 1m], RoundingPolicy.CalculationScale);

        parts.Sum().Should().Be(1.0001m);
    }
}

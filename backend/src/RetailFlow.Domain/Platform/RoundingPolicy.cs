namespace RetailFlow.Domain.Platform;

/// <summary>
/// The single place where monetary rounding happens (ADR-0006). Business code must never call
/// <see cref="decimal.Round(decimal, int)"/> directly, because the midpoint mode matters: retail
/// pricing in Bangladesh rounds half away from zero, not to even.
/// </summary>
public static class RoundingPolicy
{
    /// <summary>Decimal places kept for intermediate line calculations.</summary>
    public const int CalculationScale = 4;

    /// <summary>Decimal places used for presentation and settlement.</summary>
    public const int PresentationScale = 2;

    /// <summary>Decimal places kept for quantities.</summary>
    public const int QuantityScale = 3;

    /// <summary>
    /// Decimal places kept for unit costs, rates and conversion factors (ADR-0006). Costs need more
    /// precision than prices: a sack of rice bought at 2 350 BDT for 50 kg is 47.000000 per kg, and
    /// rounding that to four places before multiplying by a sold quantity moves the COGS.
    /// </summary>
    public const int CostScale = 6;

    public static decimal Round(decimal value, int decimals) =>
        decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

    /// <summary>Rounds an intermediate calculation (line gross, tax, discount).</summary>
    public static decimal Round4(decimal value) => Round(value, CalculationScale);

    /// <summary>Rounds a settled amount (bill total, payment, refund).</summary>
    public static decimal Round2(decimal value) => Round(value, PresentationScale);

    public static decimal RoundQuantity(decimal value) => Round(value, QuantityScale);

    /// <summary>Rounds a unit cost, a rate or a conversion factor.</summary>
    public static decimal Round6(decimal value) => Round(value, CostScale);

    /// <summary>
    /// Rounds a cash amount to a configured increment, e.g. the nearest 1.00 BDT.
    /// An increment of zero or less disables rounding.
    /// </summary>
    public static decimal RoundToIncrement(decimal value, decimal increment, CashRoundingMode mode)
    {
        if (increment <= 0m)
        {
            return Round2(value);
        }

        var units = value / increment;
        var roundedUnits = mode switch
        {
            CashRoundingMode.Up => decimal.Ceiling(units),
            CashRoundingMode.Down => decimal.Floor(units),
            CashRoundingMode.Nearest => decimal.Round(units, 0, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cash rounding mode."),
        };

        return Round2(roundedUnits * increment);
    }

    /// <summary>
    /// Distributes <paramref name="total"/> across <paramref name="weights"/> using the
    /// largest-remainder method, so the allocated parts always sum to exactly
    /// <paramref name="total"/> (ADR-0006). Used for bill-level discounts, landed costs and any
    /// proportional split where a lost or invented sub-unit would unbalance a document.
    /// </summary>
    /// <param name="total">The amount to distribute. May be negative.</param>
    /// <param name="weights">Non-negative weights, one per target line.</param>
    /// <param name="scale">Decimal places each allocated part is rounded to.</param>
    public static decimal[] Allocate(decimal total, IReadOnlyList<decimal> weights, int scale = PresentationScale)
    {
        ArgumentNullException.ThrowIfNull(weights);

        if (weights.Count == 0)
        {
            return [];
        }

        if (weights.Any(w => w < 0m))
        {
            throw new ArgumentException("Allocation weights must be non-negative.", nameof(weights));
        }

        var allocation = new decimal[weights.Count];
        var weightSum = weights.Sum();
        var roundedTotal = Round(total, scale);

        if (weightSum == 0m)
        {
            // Nothing to weight by: put the whole amount on the first line rather than silently
            // dropping it, so the document still balances.
            allocation[0] = roundedTotal;
            return allocation;
        }

        var step = Pow10(scale);
        var distributed = 0m;

        for (var i = 0; i < weights.Count; i++)
        {
            // Truncate towards zero first; the remainder is handed out below, largest share first.
            var exact = roundedTotal * weights[i] / weightSum;
            var truncated = decimal.Truncate(exact * step) / step;
            allocation[i] = truncated;
            distributed += truncated;
        }

        var residual = Round(roundedTotal - distributed, scale);
        if (residual == 0m)
        {
            return allocation;
        }

        var unit = roundedTotal >= 0m ? 1m / step : -1m / step;
        var units = (int)decimal.Round(Math.Abs(residual) * step, 0, MidpointRounding.AwayFromZero);

        var order = Enumerable.Range(0, weights.Count)
            .OrderByDescending(i => Fraction(roundedTotal * weights[i] / weightSum, step))
            .ThenByDescending(i => weights[i])
            .ThenBy(i => i)
            .ToArray();

        for (var k = 0; k < units; k++)
        {
            allocation[order[k % order.Length]] += unit;
        }

        return allocation;
    }

    private static decimal Fraction(decimal value, decimal step)
    {
        var scaled = value * step;
        return Math.Abs(scaled - decimal.Truncate(scaled));
    }

    private static decimal Pow10(int exponent)
    {
        if (exponent is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "Scale must be between 0 and 10.");
        }

        decimal result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}

/// <summary>How a cash amount is pushed onto the configured cash increment.</summary>
public enum CashRoundingMode
{
    Nearest = 0,
    Up = 1,
    Down = 2,
}

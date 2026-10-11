using System.Globalization;

namespace RetailFlow.Domain.Platform;

/// <summary>
/// A monetary amount with its currency (ADR-0006). Arithmetic across currencies throws rather than
/// guessing a rate, so a multi-currency mistake fails loudly instead of corrupting a ledger.
/// </summary>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    public const string DefaultCurrencyCode = "BDT";

    private Money(decimal amount, string currencyCode)
    {
        Amount = amount;
        CurrencyCode = currencyCode;
    }

    public decimal Amount { get; }

    public string CurrencyCode { get; }

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public static Money Zero(string currencyCode = DefaultCurrencyCode) => From(0m, currencyCode);

    public static Money From(decimal amount, string currencyCode = DefaultCurrencyCode)
    {
        var code = NormalizeCurrency(currencyCode);
        return new Money(RoundingPolicy.Round4(amount), code);
    }

    /// <summary>Creates an amount already rounded for settlement (2 dp).</summary>
    public static Money Settled(decimal amount, string currencyCode = DefaultCurrencyCode)
    {
        var code = NormalizeCurrency(currencyCode);
        return new Money(RoundingPolicy.Round2(amount), code);
    }

    public Money RoundedForSettlement() => new(RoundingPolicy.Round2(Amount), CurrencyCode);

    public Money Negated() => new(-Amount, CurrencyCode);

    public Money Abs() => new(Math.Abs(Amount), CurrencyCode);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(RoundingPolicy.Round4(Amount + other.Amount), CurrencyCode);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(RoundingPolicy.Round4(Amount - other.Amount), CurrencyCode);
    }

    public Money Multiply(decimal factor) => new(RoundingPolicy.Round4(Amount * factor), CurrencyCode);

    public Money Divide(decimal divisor)
    {
        if (divisor == 0m)
        {
            throw new DivideByZeroException("Cannot divide a monetary amount by zero.");
        }

        return new Money(RoundingPolicy.Round4(Amount / divisor), CurrencyCode);
    }

    /// <summary>Splits this amount across the given weights without losing or inventing sub-units.</summary>
    public Money[] Allocate(IReadOnlyList<decimal> weights, int scale = RoundingPolicy.PresentationScale)
    {
        var parts = RoundingPolicy.Allocate(Amount, weights, scale);
        var result = new Money[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            result[i] = new Money(parts[i], CurrencyCode);
        }

        return result;
    }

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator *(Money left, decimal factor) => left.Multiply(factor);

    public static Money operator -(Money value) => value.Negated();

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public bool Equals(Money other) =>
        Amount == other.Amount && string.Equals(CurrencyCode, other.CurrencyCode, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, CurrencyCode);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount.ToString("0.00##", CultureInfo.InvariantCulture)} {CurrencyCode}");

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(CurrencyCode, other.CurrencyCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine amounts in different currencies ({CurrencyCode} and {other.CurrencyCode}).");
        }
    }

    private static string NormalizeCurrency(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new ArgumentException("Currency code is required.", nameof(currencyCode));
        }

        var code = currencyCode.Trim().ToUpperInvariant();
        if (code.Length != 3)
        {
            throw new ArgumentException($"Currency code '{currencyCode}' must be a 3-letter ISO 4217 code.", nameof(currencyCode));
        }

        return code;
    }
}

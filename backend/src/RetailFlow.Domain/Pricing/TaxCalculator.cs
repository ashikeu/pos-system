using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Pricing;

/// <summary>
/// The tax facts used for one line, copied out of the rule version that applied at that moment.
/// Sale lines store this snapshot (ADR-0019), so a return months later reverses the tax that was
/// actually charged rather than today's rate.
/// </summary>
/// <param name="TaxClassId">The class the product belonged to.</param>
/// <param name="TaxRuleVersionId">The exact version used, for the audit trail.</param>
/// <param name="Treatment">Exclusive, inclusive, exempt or zero-rated.</param>
/// <param name="VatRate">VAT rate as a fraction.</param>
/// <param name="SdRate">Supplementary duty rate as a fraction.</param>
/// <param name="SdInVatBase">Whether SD forms part of the VAT base (A-02).</param>
/// <param name="ExemptionReason">Why tax is zero, when it is.</param>
public sealed record TaxSnapshot(
    Guid? TaxClassId,
    Guid? TaxRuleVersionId,
    TaxTreatment Treatment,
    decimal VatRate,
    decimal SdRate,
    bool SdInVatBase,
    string? ExemptionReason = null)
{
    /// <summary>
    /// The treatment for a product with no tax configuration at all: no tax, and the reason says
    /// so plainly. Used only until a tenant has configured its classes — it is visible in the
    /// response and on the receipt rather than silently charging zero as if it were policy.
    /// </summary>
    public static TaxSnapshot None { get; } = new(
        null,
        null,
        TaxTreatment.Exempt,
        0m,
        0m,
        SdInVatBase: false,
        ExemptionReason: "No tax class configured");

    /// <summary>Whether this snapshot can produce a non-zero tax amount.</summary>
    public bool IsTaxable => Treatment is TaxTreatment.Exclusive or TaxTreatment.Inclusive
        && (VatRate > 0m || SdRate > 0m);

    /// <summary>Whether the price that goes with this snapshot already contains the tax.</summary>
    public bool PriceIncludesTax => Treatment == TaxTreatment.Inclusive;
}

/// <summary>
/// One line's money, in the order the documented formulas build it up
/// (<c>docs/02-business-rules.md</c> §2). Every component is kept rather than derived on demand,
/// because the VAT return needs the taxable value and the duty separately, and a receipt needs the
/// same numbers the filing uses.
/// </summary>
/// <param name="Quantity">Quantity sold, in the variant's unit.</param>
/// <param name="UnitPrice">Resolved unit price, inclusive or exclusive per the snapshot.</param>
/// <param name="Gross"><c>round4(quantity × unitPrice)</c>.</param>
/// <param name="LineDiscount">Discount applied to this line.</param>
/// <param name="BillDiscount">Share of a bill-level discount allocated to this line.</param>
/// <param name="NetOfDiscount">Gross less both discounts; never negative.</param>
/// <param name="TaxableValue">The value VAT is charged on, duty included when it is in the base.</param>
/// <param name="SdAmount">Supplementary duty.</param>
/// <param name="VatAmount">VAT.</param>
/// <param name="LineTotal">What the customer pays for this line.</param>
/// <param name="Tax">The snapshot these numbers were produced with.</param>
public sealed record LineTaxResult(
    decimal Quantity,
    decimal UnitPrice,
    decimal Gross,
    decimal LineDiscount,
    decimal BillDiscount,
    decimal NetOfDiscount,
    decimal TaxableValue,
    decimal SdAmount,
    decimal VatAmount,
    decimal LineTotal,
    TaxSnapshot Tax)
{
    /// <summary>Total tax on the line, for a receipt that shows one figure.</summary>
    public decimal TotalTax => SdAmount + VatAmount;

    /// <summary>
    /// The value excluding all tax — the "net" a VAT return reports. Equal to
    /// <see cref="TaxableValue"/> minus the duty when the duty sits in the base.
    /// </summary>
    public decimal NetValue => TaxableValue - SdAmount;
}

/// <summary>
/// Computes VAT and supplementary duty for a line (<c>docs/02-business-rules.md</c> §2). Pure and
/// synchronous: it is the most consequential arithmetic in the system, so it takes its
/// configuration as an argument and can be pinned down by unit tests without a database.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes, four cases. With exclusive pricing the tax is added to the discounted value; with
/// inclusive pricing the same total is decomposed, and the line total is unchanged by construction
/// — which is the property that makes a tax-inclusive shelf price mean what it says. Whether the
/// duty is part of the VAT base is configuration, because its applicability varies by product and
/// SRO (A-02).
/// </para>
/// <para>
/// Everything intermediate is rounded to four places (ADR-0006). The bill is rounded to two exactly
/// once, by the sale, and the difference is carried as an explicit adjustment rather than pushed
/// back into a line.
/// </para>
/// </remarks>
public static class TaxCalculator
{
    /// <summary>
    /// Computes one line.
    /// </summary>
    /// <param name="snapshot">The tax treatment and rates that apply.</param>
    /// <param name="quantity">Quantity sold; must be positive.</param>
    /// <param name="unitPrice">Resolved unit price; must not be negative.</param>
    /// <param name="lineDiscount">Discount on this line; must not be negative.</param>
    /// <param name="billDiscount">Allocated share of a bill discount; must not be negative.</param>
    public static LineTaxResult Compute(
        TaxSnapshot snapshot,
        decimal quantity,
        decimal unitPrice,
        decimal lineDiscount = 0m,
        decimal billDiscount = 0m)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A line quantity must be positive.");
        }

        if (unitPrice < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), unitPrice, "A unit price must not be negative.");
        }

        if (lineDiscount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineDiscount), lineDiscount, "A discount must not be negative.");
        }

        if (billDiscount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(billDiscount), billDiscount, "A discount must not be negative.");
        }

        var gross = RoundingPolicy.Round4(quantity * unitPrice);
        var discount = RoundingPolicy.Round4(lineDiscount + billDiscount);

        // A discount never turns a line into a refund: the cap is the line itself. The caller is
        // told what was actually applied through LineDiscount/BillDiscount on the result.
        if (discount > gross)
        {
            throw new DomainException(
                "A discount cannot exceed the line value. Cap it at the line gross before computing tax.");
        }

        var netOfDiscount = RoundingPolicy.Round4(gross - discount);

        return snapshot.Treatment switch
        {
            TaxTreatment.Inclusive => Inclusive(snapshot, quantity, unitPrice, gross, lineDiscount, billDiscount, netOfDiscount),
            TaxTreatment.Exclusive => Exclusive(snapshot, quantity, unitPrice, gross, lineDiscount, billDiscount, netOfDiscount),

            // Exempt and zero-rated differ in the filing, not in the arithmetic: both charge
            // nothing, and the snapshot carries which one it was and why.
            _ => new LineTaxResult(
                quantity,
                unitPrice,
                gross,
                lineDiscount,
                billDiscount,
                netOfDiscount,
                TaxableValue: netOfDiscount,
                SdAmount: 0m,
                VatAmount: 0m,
                LineTotal: netOfDiscount,
                Tax: snapshot),
        };
    }

    /// <summary>
    /// Price excludes tax, so duty and VAT are added:
    /// <c>sd = net × s</c>, <c>vat = (net + sd) × v</c> when the duty is in the base.
    /// </summary>
    private static LineTaxResult Exclusive(
        TaxSnapshot snapshot,
        decimal quantity,
        decimal unitPrice,
        decimal gross,
        decimal lineDiscount,
        decimal billDiscount,
        decimal netOfDiscount)
    {
        var sdAmount = RoundingPolicy.Round4(netOfDiscount * snapshot.SdRate);
        var vatBase = snapshot.SdInVatBase
            ? RoundingPolicy.Round4(netOfDiscount + sdAmount)
            : netOfDiscount;
        var vatAmount = RoundingPolicy.Round4(vatBase * snapshot.VatRate);

        return new LineTaxResult(
            quantity,
            unitPrice,
            gross,
            lineDiscount,
            billDiscount,
            netOfDiscount,
            TaxableValue: RoundingPolicy.Round4(netOfDiscount + sdAmount),
            SdAmount: sdAmount,
            VatAmount: vatAmount,
            LineTotal: RoundingPolicy.Round4(netOfDiscount + sdAmount + vatAmount),
            Tax: snapshot);
    }

    /// <summary>
    /// Price contains the tax, so the total is decomposed. The divisor is
    /// <c>1 + s + v + s·v</c> when the duty is in the VAT base and <c>1 + s + v</c> when it is not;
    /// either way <c>base + sd + vat = net</c>, so the line total is what was on the shelf label.
    /// </summary>
    private static LineTaxResult Inclusive(
        TaxSnapshot snapshot,
        decimal quantity,
        decimal unitPrice,
        decimal gross,
        decimal lineDiscount,
        decimal billDiscount,
        decimal netOfDiscount)
    {
        var divisor = snapshot.SdInVatBase
            ? 1m + snapshot.SdRate + snapshot.VatRate + (snapshot.SdRate * snapshot.VatRate)
            : 1m + snapshot.SdRate + snapshot.VatRate;

        if (divisor <= 0m)
        {
            throw new DomainException("Tax rates produce a non-positive divisor; check the configured rates.");
        }

        var baseValue = RoundingPolicy.Round4(netOfDiscount / divisor);
        var sdAmount = RoundingPolicy.Round4(baseValue * snapshot.SdRate);
        var vatBase = snapshot.SdInVatBase ? RoundingPolicy.Round4(baseValue + sdAmount) : baseValue;
        var vatAmount = RoundingPolicy.Round4(vatBase * snapshot.VatRate);

        // Rounding each component independently can leave a sub-unit difference against the price
        // the customer was shown. The line total is the price, so the difference is absorbed into
        // VAT — the largest component and the one the filing reports gross — rather than being
        // left as a penny that makes the receipt not add up.
        var taxableValue = RoundingPolicy.Round4(baseValue + sdAmount);
        var residual = RoundingPolicy.Round4(netOfDiscount - taxableValue - vatAmount);

        if (residual != 0m)
        {
            vatAmount = RoundingPolicy.Round4(vatAmount + residual);
        }

        return new LineTaxResult(
            quantity,
            unitPrice,
            gross,
            lineDiscount,
            billDiscount,
            netOfDiscount,
            TaxableValue: taxableValue,
            SdAmount: sdAmount,
            VatAmount: vatAmount,
            LineTotal: netOfDiscount,
            Tax: snapshot);
    }

    /// <summary>
    /// Allocates a bill-level discount across lines in proportion to each line's value, using the
    /// largest-remainder method so the parts sum to the discount exactly (ADR-0006). A proportional
    /// split that loses a sub-unit would make the bill not add up.
    /// </summary>
    /// <param name="billDiscount">The discount to spread; must not be negative.</param>
    /// <param name="lineValues">Each line's value after its own line discount.</param>
    public static decimal[] AllocateBillDiscount(decimal billDiscount, IReadOnlyList<decimal> lineValues)
    {
        ArgumentNullException.ThrowIfNull(lineValues);

        if (billDiscount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(billDiscount), billDiscount, "A discount must not be negative.");
        }

        if (lineValues.Count == 0)
        {
            return [];
        }

        var total = lineValues.Sum();

        if (billDiscount > total)
        {
            throw new DomainException("A bill discount cannot exceed the value of the lines it is spread over.");
        }

        return total == 0m
            ? new decimal[lineValues.Count]
            : RoundingPolicy.Allocate(billDiscount, lineValues, RoundingPolicy.CalculationScale);
    }
}

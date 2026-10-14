using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Pricing;

/// <summary>
/// Where a price came from, in the order the resolution rule applies them
/// (<c>docs/02-business-rules.md</c> §1). The numeric order <em>is</em> the precedence, so a lower
/// value wins; a receipt and an exception report both show which source answered.
/// </summary>
public enum PriceSource
{
    /// <summary>A cashier's authorised override on the line. Beats everything.</summary>
    ManualOverride = 0,

    /// <summary>A price agreed with one customer (Phase 8 supplies these).</summary>
    CustomerPrice = 1,

    /// <summary>An item in a price list that applies to the customer's group.</summary>
    CustomerGroupPriceList = 2,

    /// <summary>A per-branch price for the variant.</summary>
    BranchPriceOverride = 3,

    /// <summary>An item in the tenant's default price list.</summary>
    DefaultPriceList = 4,

    /// <summary>The variant's own base price. Always available, so resolution never fails.</summary>
    VariantBasePrice = 5,
}

/// <summary>
/// A price that might apply, with enough context to rank it against the others.
/// </summary>
/// <param name="Source">Which kind of source produced it.</param>
/// <param name="UnitPrice">The price itself.</param>
/// <param name="SourceId">The price list, override or customer-price row it came from.</param>
/// <param name="Priority">Priority of the owning price list; higher wins within one source.</param>
/// <param name="MinQuantity">
/// Quantity from which the candidate is valid, for wholesale tiers. A candidate whose minimum is
/// above the quantity being sold is not considered at all.
/// </param>
public sealed record PriceCandidate(
    PriceSource Source,
    decimal UnitPrice,
    Guid? SourceId = null,
    int Priority = 0,
    decimal MinQuantity = 0m);

/// <summary>The answer: one price, and where it came from.</summary>
/// <param name="UnitPrice">The resolved unit price.</param>
/// <param name="Source">Which source won.</param>
/// <param name="SourceId">That source's row, when it has one.</param>
/// <param name="MinQuantity">The tier that applied, for display ("from 12 pieces").</param>
public sealed record ResolvedPrice(
    decimal UnitPrice,
    PriceSource Source,
    Guid? SourceId,
    decimal MinQuantity)
{
    /// <summary>Whether the price came from somewhere other than the variant's base price.</summary>
    public bool IsOverridden => Source != PriceSource.VariantBasePrice;
}

/// <summary>
/// Picks the price that applies (<c>docs/02-business-rules.md</c> §1). Pure, so the precedence rule
/// can be pinned down by unit tests: this is the arithmetic a shop notices immediately and trusts
/// least when it is wrong.
/// </summary>
public static class PriceResolution
{
    /// <summary>
    /// Resolves the price for a quantity. Candidates whose minimum quantity is above
    /// <paramref name="quantity"/> are ignored; of the rest, the first source in precedence order
    /// wins, then the highest price-list priority, then the highest qualifying tier — so a
    /// wholesale tier at 12 beats the same list's open-ended price when 12 are sold.
    /// </summary>
    /// <param name="candidates">Every price that might apply. May be empty.</param>
    /// <param name="basePrice">The variant's base price, the always-present fallback.</param>
    /// <param name="quantity">Quantity being sold; decides which tiers qualify.</param>
    public static ResolvedPrice Select(
        IEnumerable<PriceCandidate> candidates,
        decimal basePrice,
        decimal quantity = 1m)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A quantity must be positive.");
        }

        if (basePrice < 0m)
        {
            throw new DomainException("A base price must not be negative.");
        }

        var winner = candidates
            .Where(candidate => candidate.MinQuantity <= quantity)
            .Where(candidate => candidate.UnitPrice >= 0m)
            .OrderBy(candidate => (int)candidate.Source)
            .ThenByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.MinQuantity)

            // A deterministic last tiebreak: two rows that are equal on everything above must still
            // resolve the same way on every till and after every sync.
            .ThenBy(candidate => candidate.UnitPrice)
            .FirstOrDefault();

        return winner is null
            ? new ResolvedPrice(RoundingPolicy.Round4(basePrice), PriceSource.VariantBasePrice, null, 0m)
            : new ResolvedPrice(
                RoundingPolicy.Round4(winner.UnitPrice),
                winner.Source,
                winner.SourceId,
                winner.MinQuantity);
    }
}

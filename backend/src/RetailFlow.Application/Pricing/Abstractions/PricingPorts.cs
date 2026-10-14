using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Pricing.Abstractions;

/// <summary>
/// What to price, and for whom. Everything except the variant is optional, so the same query serves
/// a price check on a web screen and a line on a till.
/// </summary>
/// <param name="VariantId">The variant being priced.</param>
/// <param name="Quantity">Quantity, which decides which wholesale tier qualifies.</param>
/// <param name="BranchId">Branch, for branch overrides and branch-scoped lists.</param>
/// <param name="CustomerGroupId">Customer group, for its price list.</param>
/// <param name="ManualPrice">
/// An authorised override keyed by a cashier. The caller is responsible for having checked
/// <c>sales.price.override</c> first; resolution only ranks it (docs/02 §1).
/// </param>
/// <param name="AsOfUtc">The instant to resolve at. Defaults to now; a sync replay passes the original.</param>
public sealed record PriceQuery(
    Guid VariantId,
    decimal Quantity = 1m,
    Guid? BranchId = null,
    Guid? CustomerGroupId = null,
    decimal? ManualPrice = null,
    DateTimeOffset? AsOfUtc = null);

/// <summary>
/// Resolves a price through the documented order (docs/02 §1). A port rather than a static helper
/// because it reads price lists and overrides; the ranking itself is pure
/// (<see cref="PriceResolution"/>).
/// </summary>
public interface IPriceResolver
{
    Task<ResolvedPrice> ResolveAsync(PriceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds the tax treatment and rates in force for a product at an instant (ADR-0019). Returns a
/// snapshot, which is what a sale line stores so a return reverses the tax that was charged.
/// </summary>
public interface ITaxResolver
{
    /// <summary>
    /// Resolves the snapshot for a tax class at a branch. A branch-specific rule wins over the
    /// organization-wide one; a class with no rule in force yet resolves to no tax, with the
    /// reason recorded rather than silently charging zero.
    /// </summary>
    Task<TaxSnapshot> ResolveAsync(
        Guid? taxClassId,
        Guid? branchId = null,
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Data access for pricing and tax configuration. The reads are deliberately narrow: the POS price
/// path runs on every scan, so it asks for the few rows that can win rather than loading lists.
/// </summary>
public interface IPricingRepository
{
    // --- tax ---

    Task<TaxClass?> FindTaxClassAsync(Guid taxClassId, CancellationToken cancellationToken = default);

    Task<TaxClass?> FindDefaultTaxClassAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxClass>> GetTaxClassesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> TaxClassCodeExistsAsync(
        string code,
        Guid? exceptTaxClassId = null,
        CancellationToken cancellationToken = default);

    void AddTaxClass(TaxClass taxClass);

    /// <summary>Loads a rule with its versions, for adding the next one.</summary>
    Task<TaxRule?> FindTaxRuleAsync(Guid taxRuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules for a tax class, with versions. Both the branch rule and the organization-wide one
    /// come back, because which applies depends on the branch in scope.
    /// </summary>
    Task<IReadOnlyList<TaxRule>> GetTaxRulesForClassAsync(
        Guid taxClassId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaxRule>> GetTaxRulesAsync(CancellationToken cancellationToken = default);

    void AddTaxRule(TaxRule rule);

    // --- price lists ---

    Task<PriceList?> FindPriceListAsync(Guid priceListId, CancellationToken cancellationToken = default);

    Task<PriceList?> FindDefaultPriceListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PriceList>> GetPriceListsAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> PriceListCodeExistsAsync(
        string code,
        Guid? exceptPriceListId = null,
        CancellationToken cancellationToken = default);

    void AddPriceList(PriceList priceList);

    /// <summary>
    /// Every price-list item that could price this variant for this scope, with the owning list's
    /// scope and priority. One query instead of "load the lists, then their items".
    /// </summary>
    Task<IReadOnlyList<PriceCandidateRow>> GetPriceCandidatesAsync(
        Guid productVariantId,
        Guid? branchId,
        Guid? customerGroupId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);

    // --- branch overrides ---

    Task<BranchPriceOverride?> FindBranchPriceAsync(
        Guid branchId,
        Guid productVariantId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BranchPriceOverride>> GetBranchPricesAsync(
        Guid branchId,
        CancellationToken cancellationToken = default);

    void AddBranchPrice(BranchPriceOverride branchPrice);

    // --- discounts ---

    Task<DiscountRule?> FindDiscountRuleAsync(Guid discountRuleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscountRule>> GetDiscountRulesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> DiscountCodeExistsAsync(
        string code,
        Guid? exceptDiscountRuleId = null,
        CancellationToken cancellationToken = default);

    void AddDiscountRule(DiscountRule rule);

    /// <summary>
    /// The standing discount rules that could apply to a variant at an instant — its own rules plus
    /// those on its category and brand, plus the bill-level ones.
    /// </summary>
    Task<IReadOnlyList<DiscountRule>> GetApplicableDiscountsAsync(
        Guid productVariantId,
        Guid? branchId,
        Guid? customerGroupId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A price-list item together with the scope of the list that holds it, flattened so the resolver
/// can rank candidates without loading aggregates.
/// </summary>
/// <param name="PriceListId">The list.</param>
/// <param name="UnitPrice">The price.</param>
/// <param name="MinQuantity">Quantity from which it applies.</param>
/// <param name="Priority">The list's priority.</param>
/// <param name="IsDefaultList">Whether this is the tenant's default list.</param>
/// <param name="CustomerGroupId">The group the list is scoped to, when it is.</param>
/// <param name="BranchId">The branch the list is scoped to, when it is.</param>
public sealed record PriceCandidateRow(
    Guid PriceListId,
    decimal UnitPrice,
    decimal MinQuantity,
    int Priority,
    bool IsDefaultList,
    Guid? CustomerGroupId,
    Guid? BranchId);

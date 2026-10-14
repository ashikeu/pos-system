using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Pricing.Abstractions;
using RetailFlow.Domain.Pricing;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Pricing.Persistence;

/// <summary>
/// Pricing and tax data access. The two reads on the POS path —
/// <see cref="GetPriceCandidatesAsync"/> and the tax rules — are written as single indexed queries,
/// because they run on every scan.
/// </summary>
internal sealed class PricingRepository : IPricingRepository
{
    private readonly RetailFlowDbContext _context;

    public PricingRepository(RetailFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    // ------------------------------------------------------------------ tax

    public Task<TaxClass?> FindTaxClassAsync(Guid taxClassId, CancellationToken cancellationToken = default) =>
        _context.TaxClasses.FirstOrDefaultAsync(t => t.Id == taxClassId, cancellationToken);

    public Task<TaxClass?> FindDefaultTaxClassAsync(CancellationToken cancellationToken = default) =>
        _context.TaxClasses.FirstOrDefaultAsync(t => t.IsDefault, cancellationToken);

    public async Task<IReadOnlyList<TaxClass>> GetTaxClassesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.TaxClasses.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(t => t.IsActive);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TaxClassCodeExistsAsync(
        string code,
        Guid? exceptTaxClassId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.TaxClasses
            .AnyAsync(
                t => t.Code == normalized && (exceptTaxClassId == null || t.Id != exceptTaxClassId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddTaxClass(TaxClass taxClass)
    {
        ArgumentNullException.ThrowIfNull(taxClass);
        _context.TaxClasses.Add(taxClass);
    }

    public Task<TaxRule?> FindTaxRuleAsync(Guid taxRuleId, CancellationToken cancellationToken = default) =>
        _context.TaxRules
            .Include(r => r.Versions)
            .FirstOrDefaultAsync(r => r.Id == taxRuleId, cancellationToken);

    public async Task<IReadOnlyList<TaxRule>> GetTaxRulesForClassAsync(
        Guid taxClassId,
        CancellationToken cancellationToken = default) =>
        await _context.TaxRules
            .Include(r => r.Versions)
            .Where(r => r.TaxClassId == taxClassId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<TaxRule>> GetTaxRulesAsync(CancellationToken cancellationToken = default) =>
        await _context.TaxRules
            .Include(r => r.Versions)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void AddTaxRule(TaxRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _context.TaxRules.Add(rule);
    }

    // ------------------------------------------------------------------ price lists

    public Task<PriceList?> FindPriceListAsync(Guid priceListId, CancellationToken cancellationToken = default) =>
        _context.PriceLists
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Id == priceListId, cancellationToken);

    public Task<PriceList?> FindDefaultPriceListAsync(CancellationToken cancellationToken = default) =>
        _context.PriceLists.FirstOrDefaultAsync(l => l.IsDefault, cancellationToken);

    public async Task<IReadOnlyList<PriceList>> GetPriceListsAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PriceLists.Include(l => l.Items).AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(l => l.IsActive);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PriceListCodeExistsAsync(
        string code,
        Guid? exceptPriceListId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.PriceLists
            .AnyAsync(
                l => l.Code == normalized && (exceptPriceListId == null || l.Id != exceptPriceListId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddPriceList(PriceList priceList)
    {
        ArgumentNullException.ThrowIfNull(priceList);
        _context.PriceLists.Add(priceList);
    }

    /// <summary>
    /// Every price that could apply to this variant for this scope, flattened with the owning
    /// list's scope and priority. One query on the scan path: the alternative is loading the
    /// tenant's lists and filtering in memory, which gets slower as the tenant grows.
    /// </summary>
    public async Task<IReadOnlyList<PriceCandidateRow>> GetPriceCandidatesAsync(
        Guid productVariantId,
        Guid? branchId,
        Guid? customerGroupId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default) =>
        await (from item in _context.PriceListItems.AsNoTracking()
               join list in _context.PriceLists.AsNoTracking() on item.PriceListId equals list.Id
               where item.ProductVariantId == productVariantId
                   && list.IsActive
                   && (list.EffectiveFromUtc == null || list.EffectiveFromUtc <= asOfUtc)
                   && (list.EffectiveToUtc == null || asOfUtc < list.EffectiveToUtc)

                   // A list scoped to a branch or a group only applies to that branch or group; an
                   // unscoped list applies to everyone.
                   && (list.BranchId == null || list.BranchId == branchId)
                   && (list.CustomerGroupId == null || list.CustomerGroupId == customerGroupId)
               select new PriceCandidateRow(
                   list.Id,
                   item.UnitPrice,
                   item.MinQuantity,
                   list.Priority,
                   list.IsDefault,
                   list.CustomerGroupId,
                   list.BranchId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // ------------------------------------------------------------------ branch overrides

    public Task<BranchPriceOverride?> FindBranchPriceAsync(
        Guid branchId,
        Guid productVariantId,
        CancellationToken cancellationToken = default) =>
        _context.BranchPriceOverrides.FirstOrDefaultAsync(
            p => p.BranchId == branchId && p.ProductVariantId == productVariantId,
            cancellationToken);

    public async Task<IReadOnlyList<BranchPriceOverride>> GetBranchPricesAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        await _context.BranchPriceOverrides
            .Where(p => p.BranchId == branchId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void AddBranchPrice(BranchPriceOverride branchPrice)
    {
        ArgumentNullException.ThrowIfNull(branchPrice);
        _context.BranchPriceOverrides.Add(branchPrice);
    }

    // ------------------------------------------------------------------ discounts

    public Task<DiscountRule?> FindDiscountRuleAsync(
        Guid discountRuleId,
        CancellationToken cancellationToken = default) =>
        _context.DiscountRules.FirstOrDefaultAsync(r => r.Id == discountRuleId, cancellationToken);

    public async Task<IReadOnlyList<DiscountRule>> GetDiscountRulesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _context.DiscountRules.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(r => r.IsActive);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DiscountCodeExistsAsync(
        string code,
        Guid? exceptDiscountRuleId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);

        return await _context.DiscountRules
            .AnyAsync(
                r => r.Code == normalized && (exceptDiscountRuleId == null || r.Id != exceptDiscountRuleId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddDiscountRule(DiscountRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _context.DiscountRules.Add(rule);
    }

    public async Task<IReadOnlyList<DiscountRule>> GetApplicableDiscountsAsync(
        Guid productVariantId,
        Guid? branchId,
        Guid? customerGroupId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        // The variant's product decides which category and brand rules can apply, so those ids are
        // read first; the rules themselves then come back in one query.
        var classification = await _context.ProductVariants
            .AsNoTracking()
            .Where(v => v.Id == productVariantId)
            .Join(
                _context.Products.AsNoTracking(),
                v => v.ProductId,
                p => p.Id,
                (v, p) => new { p.CategoryId, p.BrandId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var categoryId = classification?.CategoryId;
        var brandId = classification?.BrandId;

        return await _context.DiscountRules
            .AsNoTracking()
            .Where(r => r.IsActive
                && (r.EffectiveFromUtc == null || r.EffectiveFromUtc <= asOfUtc)
                && (r.EffectiveToUtc == null || asOfUtc < r.EffectiveToUtc)
                && (r.BranchId == null || r.BranchId == branchId)
                && (r.CustomerGroupId == null || r.CustomerGroupId == customerGroupId)
                && (r.Scope == DiscountScope.Bill
                    || (r.Scope == DiscountScope.Variant && r.TargetId == productVariantId)
                    || (r.Scope == DiscountScope.Category && categoryId != null && r.TargetId == categoryId)
                    || (r.Scope == DiscountScope.Brand && brandId != null && r.TargetId == brandId)))
            .OrderByDescending(r => r.Priority)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

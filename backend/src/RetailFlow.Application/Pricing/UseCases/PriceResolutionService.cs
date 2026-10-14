using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Pricing.Abstractions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Pricing.UseCases;

/// <summary>
/// Turns the stored price configuration into candidates and lets the domain rank them
/// (<c>docs/02-business-rules.md</c> §1). The split is deliberate: gathering is I/O and ranking is
/// a rule, and only the rule has to be provably right on every till.
/// </summary>
public sealed class PriceResolutionService : IPriceResolver
{
    private readonly IPricingRepository _pricing;
    private readonly ICatalogRepository _catalog;
    private readonly IClock _clock;

    public PriceResolutionService(IPricingRepository pricing, ICatalogRepository catalog, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clock);

        _pricing = pricing;
        _catalog = catalog;
        _clock = clock;
    }

    public async Task<ResolvedPrice> ResolveAsync(
        PriceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var variant = await _catalog.FindVariantAsync(query.VariantId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("That product variant does not exist in this organization.");

        var asOf = query.AsOfUtc ?? _clock.UtcNow;
        var quantity = query.Quantity <= 0m ? 1m : query.Quantity;

        var candidates = new List<PriceCandidate>();

        if (query.ManualPrice is { } manual)
        {
            candidates.Add(new PriceCandidate(PriceSource.ManualOverride, manual));
        }

        var rows = await _pricing
            .GetPriceCandidatesAsync(query.VariantId, query.BranchId, query.CustomerGroupId, asOf, cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            // A list scoped to a customer group outranks the tenant default, and a branch-scoped
            // list is treated as a branch price: both follow the documented order rather than the
            // order the rows happened to come back in.
            var source = row switch
            {
                { CustomerGroupId: not null } => PriceSource.CustomerGroupPriceList,
                { BranchId: not null } => PriceSource.BranchPriceOverride,
                _ => PriceSource.DefaultPriceList,
            };

            candidates.Add(new PriceCandidate(
                source,
                row.UnitPrice,
                row.PriceListId,
                row.Priority,
                row.MinQuantity));
        }

        if (query.BranchId is { } branchId)
        {
            var branchPrice = await _pricing
                .FindBranchPriceAsync(branchId, query.VariantId, cancellationToken)
                .ConfigureAwait(false);

            if (branchPrice is not null && branchPrice.IsEffectiveAt(asOf))
            {
                candidates.Add(new PriceCandidate(
                    PriceSource.BranchPriceOverride,
                    branchPrice.UnitPrice,
                    branchPrice.Id,

                    // A direct branch price beats a branch-scoped price list, which is the whole
                    // point of having it: it is the per-shop exception.
                    Priority: int.MaxValue));
            }
        }

        return PriceResolution.Select(candidates, variant.BasePrice, quantity);
    }
}

/// <summary>
/// Finds the tax rates in force for a product at an instant (ADR-0019), preferring a branch-specific
/// rule over the organization-wide one.
/// </summary>
public sealed class TaxResolutionService : ITaxResolver
{
    private readonly IPricingRepository _pricing;
    private readonly IClock _clock;

    public TaxResolutionService(IPricingRepository pricing, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(clock);

        _pricing = pricing;
        _clock = clock;
    }

    public async Task<TaxSnapshot> ResolveAsync(
        Guid? taxClassId,
        Guid? branchId = null,
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var asOf = asOfUtc ?? _clock.UtcNow;

        var taxClass = taxClassId is { } id
            ? await _pricing.FindTaxClassAsync(id, cancellationToken).ConfigureAwait(false)
            : await _pricing.FindDefaultTaxClassAsync(cancellationToken).ConfigureAwait(false);

        if (taxClass is null)
        {
            return TaxSnapshot.None;
        }

        if (taxClass.Treatment is TaxTreatment.Exempt or TaxTreatment.ZeroRated)
        {
            return new TaxSnapshot(
                taxClass.Id,
                null,
                taxClass.Treatment,
                0m,
                0m,
                SdInVatBase: false,
                taxClass.ExemptionReason);
        }

        var rules = await _pricing
            .GetTaxRulesForClassAsync(taxClass.Id, cancellationToken)
            .ConfigureAwait(false);

        // A branch rule wins over the organization-wide one; among equals, the version that started
        // most recently. There is no "nearest match" cleverness: either a rule names this branch or
        // it names none.
        var version = rules
            .Where(rule => rule.BranchId is null || rule.BranchId == branchId)
            .OrderByDescending(rule => rule.BranchId == branchId && branchId is not null)
            .Select(rule => rule.CurrentVersion(asOf))
            .FirstOrDefault(v => v is not null);

        if (version is null)
        {
            // The class exists but has no rate in force: charging zero silently would understate a
            // VAT return, so the reason travels with the line and onto the receipt.
            return new TaxSnapshot(
                taxClass.Id,
                null,
                TaxTreatment.Exempt,
                0m,
                0m,
                SdInVatBase: false,
                $"No tax rate in force for class {taxClass.Code}");
        }

        return new TaxSnapshot(
            taxClass.Id,
            version.Id,
            version.PriceIncludesTax ? TaxTreatment.Inclusive : TaxTreatment.Exclusive,
            version.VatRate,
            version.SdRate,
            version.SdInVatBase);
    }
}

using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Pricing.Abstractions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Catalog.UseCases;

/// <summary>
/// What the till does on every scan: resolve the code, resolve the price, resolve the tax, and
/// return the line as it would be rung up (FR-POS-01, FR-CAT-03).
/// </summary>
/// <remarks>
/// <para>
/// One round trip on purpose. A cashier working through a queue notices a second request, and an
/// offline-capable client needs the same answer shape whether it came from the server or from its
/// own cache (ADR-0017).
/// </para>
/// <para>
/// A withdrawn product still resolves, with <c>IsSellable = false</c>: the till can then say "this
/// product has been withdrawn" rather than "unknown barcode", which is the difference between a
/// cashier calling a manager and a cashier re-scanning four times.
/// </para>
/// </remarks>
public sealed class LookupProductHandler : IUseCase<LookupProductRequest, LookupProductResponse>
{
    private readonly ICatalogRepository _catalog;
    private readonly IPriceResolver _prices;
    private readonly ITaxResolver _taxes;
    private readonly ITenantContext _tenant;

    public LookupProductHandler(
        ICatalogRepository catalog,
        IPriceResolver prices,
        ITaxResolver taxes,
        ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(taxes);
        ArgumentNullException.ThrowIfNull(tenant);

        _catalog = catalog;
        _prices = prices;
        _taxes = taxes;
        _tenant = tenant;
    }

    public async Task<Result<LookupProductResponse>> HandleAsync(
        LookupProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Error.Validation("catalog.code_required", "Scan or type a code.");
        }

        var lookup = await _catalog
            .LookupAsync(request.Code.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (lookup is null)
        {
            return CatalogErrors.NotFoundByCode(request.Code.Trim());
        }

        var branchId = request.BranchId ?? _tenant.BranchId;

        // A case barcode sells its pack quantity per scan, so the quantity priced is the scanned
        // quantity times that — which is also what decides whether a wholesale tier qualifies.
        var quantity = RoundingPolicy.RoundQuantity(
            (request.Quantity <= 0m ? 1m : request.Quantity) * lookup.PackQuantity);

        var price = await _prices
            .ResolveAsync(
                new PriceQuery(lookup.VariantId, quantity, branchId, request.CustomerGroupId),
                cancellationToken)
            .ConfigureAwait(false);

        var tax = await _taxes
            .ResolveAsync(lookup.TaxClassId, branchId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var line = TaxCalculator.Compute(tax, quantity, price.UnitPrice);

        return new LookupProductResponse(
            lookup,
            new ResolvedPriceResponse(price.UnitPrice, price.Source, price.SourceId, price.MinQuantity),
            new TaxSnapshotResponse(
                tax.TaxClassId,
                tax.TaxRuleVersionId,
                tax.Treatment,
                tax.VatRate,
                tax.SdRate,
                tax.SdInVatBase,
                tax.ExemptionReason),
            new LineTaxResponse(
                line.Quantity,
                line.UnitPrice,
                line.Gross,
                line.NetOfDiscount,
                line.TaxableValue,
                line.SdAmount,
                line.VatAmount,
                line.LineTotal));
    }
}

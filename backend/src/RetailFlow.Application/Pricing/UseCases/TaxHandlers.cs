using RetailFlow.Application.Catalog.UseCases;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Pricing.Abstractions;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Pricing.UseCases;

/// <summary>Error codes the pricing module returns; part of the API contract (ADR-0014).</summary>
public static class PricingErrors
{
    public static Error TaxClassNotFound() => Error.NotFound(
        "pricing.tax_class_not_found",
        "That tax class does not exist.");

    public static Error PriceListNotFound() => Error.NotFound(
        "pricing.price_list_not_found",
        "That price list does not exist.");

    public static Error DiscountRuleNotFound() => Error.NotFound(
        "pricing.discount_rule_not_found",
        "That discount rule does not exist.");

    public static Error CodeTaken(string what, string code) => Error.Conflict(
        $"pricing.{what}_code_taken",
        $"The code '{code}' is already used by another {what}.");

    public static Error Invalid(string message) => Error.Validation("pricing.invalid", message);
}

/// <summary>Lists tax classes with their rules and rate versions.</summary>
public sealed class ListTaxClassesHandler : IUseCase<ListTaxClassesRequest, IReadOnlyList<TaxClassResponse>>
{
    private readonly IPricingRepository _repository;

    public ListTaxClassesHandler(IPricingRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<TaxClassResponse>>> HandleAsync(
        ListTaxClassesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var classes = await _repository
            .GetTaxClassesAsync(request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        var rules = await _repository.GetTaxRulesAsync(cancellationToken).ConfigureAwait(false);

        return classes
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(taxClass => PricingMapping.ToResponse(
                taxClass,
                rules.Where(r => r.TaxClassId == taxClass.Id).ToArray()))
            .ToArray();
    }
}

/// <summary>Creates a tax class.</summary>
public sealed class CreateTaxClassHandler : IUseCase<CreateTaxClassRequest, TaxClassResponse>
{
    private readonly IPricingRepository _repository;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public CreateTaxClassHandler(
        IPricingRepository repository,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<TaxClassResponse>> HandleAsync(
        CreateTaxClassRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.TaxClassCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return PricingErrors.CodeTaken("tax_class", code);
        }

        TaxClass taxClass;

        try
        {
            taxClass = TaxClass.Create(_guids.NewId(), _tenant.OrganizationId, code, request.Name, request.Treatment);

            if (request.Treatment is TaxTreatment.Exempt or TaxTreatment.ZeroRated)
            {
                taxClass.SetExemption(request.Treatment, request.ExemptionReason);
            }
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        if (request.MakeDefault)
        {
            var current = await _repository.FindDefaultTaxClassAsync(cancellationToken).ConfigureAwait(false);
            current?.ClearDefault();
            taxClass.MakeDefault();
        }

        _repository.AddTaxClass(taxClass);

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.tax_class_created",
                nameof(TaxClass),
                taxClass.Id,
                After: new { taxClass.Code, taxClass.Name, taxClass.Treatment, taxClass.IsDefault }),
            cancellationToken).ConfigureAwait(false);

        return PricingMapping.ToResponse(taxClass, []);
    }
}

/// <summary>Changes a tax class's name, treatment or default flag.</summary>
public sealed class UpdateTaxClassHandler : IUseCase<UpdateTaxClassRequest, TaxClassResponse>
{
    private readonly IPricingRepository _repository;
    private readonly IAuditWriter _audit;

    public UpdateTaxClassHandler(IPricingRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<TaxClassResponse>> HandleAsync(
        UpdateTaxClassRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var taxClass = await _repository
            .FindTaxClassAsync(request.TaxClassId, cancellationToken)
            .ConfigureAwait(false);

        if (taxClass is null)
        {
            return PricingErrors.TaxClassNotFound();
        }

        var before = new { taxClass.Name, taxClass.Treatment, taxClass.IsDefault };

        try
        {
            taxClass.Rename(request.Name);
            taxClass.SetExemption(request.Treatment, request.ExemptionReason);
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        if (request.MakeDefault && !taxClass.IsDefault)
        {
            var current = await _repository.FindDefaultTaxClassAsync(cancellationToken).ConfigureAwait(false);
            current?.ClearDefault();
            taxClass.MakeDefault();
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.tax_class_updated",
                nameof(TaxClass),
                taxClass.Id,
                before,
                new { taxClass.Name, taxClass.Treatment, taxClass.IsDefault }),
            cancellationToken).ConfigureAwait(false);

        var rules = await _repository
            .GetTaxRulesForClassAsync(taxClass.Id, cancellationToken)
            .ConfigureAwait(false);

        return PricingMapping.ToResponse(taxClass, rules);
    }
}

/// <summary>
/// Opens a new rate version (ADR-0019). The change is audited with both rates and the SRO
/// reference, because a VAT rate change is the kind of thing an auditor asks about by date.
/// </summary>
public sealed class AddTaxRateVersionHandler : IUseCase<AddTaxRateVersionRequest, TaxClassResponse>
{
    private readonly IPricingRepository _repository;
    private readonly ITenancyRepository _tenancy;
    private readonly IEffectiveSettingsProvider _settings;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public AddTaxRateVersionHandler(
        IPricingRepository repository,
        ITenancyRepository tenancy,
        IEffectiveSettingsProvider settings,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenancy = tenancy;
        _settings = settings;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<TaxClassResponse>> HandleAsync(
        AddTaxRateVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var taxClass = await _repository
            .FindTaxClassAsync(request.TaxClassId, cancellationToken)
            .ConfigureAwait(false);

        if (taxClass is null)
        {
            return PricingErrors.TaxClassNotFound();
        }

        if (taxClass.Treatment is TaxTreatment.Exempt or TaxTreatment.ZeroRated)
        {
            return PricingErrors.Invalid(
                "An exempt or zero-rated class charges nothing, so it takes no rates. Change its treatment first.");
        }

        if (request.BranchId is { } branchId
            && await _tenancy.FindBranchAsync(branchId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("tenancy.branch_not_found", "That branch does not exist.");
        }

        // Defaults to the organization's tax-display setting, so "prices include tax" means one
        // thing across the system instead of two that can disagree (ADR-0031).
        var priceIncludesTax = request.PriceIncludesTax;

        if (priceIncludesTax is null)
        {
            var effective = await _settings
                .GetAsync(_tenant.OrganizationId, request.BranchId, null, cancellationToken)
                .ConfigureAwait(false);

            priceIncludesTax = effective.PriceIncludesTax;
        }

        var rules = await _repository
            .GetTaxRulesForClassAsync(taxClass.Id, cancellationToken)
            .ConfigureAwait(false);

        var rule = rules.FirstOrDefault(r => r.BranchId == request.BranchId);

        if (rule is null)
        {
            rule = TaxRule.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                taxClass.Id,
                request.BranchId is null ? taxClass.Name : $"{taxClass.Name} (branch)",
                request.BranchId);

            _repository.AddTaxRule(rule);
        }

        TaxRuleVersion version;

        try
        {
            version = rule.AddVersion(
                _guids.NewId(),
                request.VatRate,
                request.SdRate,
                request.SdInVatBase,
                priceIncludesTax.Value,
                request.EffectiveFromUtc,
                request.SroReference,
                request.Notes);
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.tax_rate_version_added",
                nameof(TaxRuleVersion),
                version.Id,
                After: new
                {
                    taxClass.Code,
                    version.Version,
                    version.VatRate,
                    version.SdRate,
                    version.SdInVatBase,
                    version.PriceIncludesTax,
                    version.EffectiveFromUtc,
                    version.SroReference,
                },
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["taxClassId"] = taxClass.Id.ToString(),
                    ["branchId"] = request.BranchId?.ToString() ?? "all",
                }),
            cancellationToken).ConfigureAwait(false);

        var updated = await _repository
            .GetTaxRulesForClassAsync(taxClass.Id, cancellationToken)
            .ConfigureAwait(false);

        return PricingMapping.ToResponse(taxClass, updated.Count > 0 ? updated : [rule]);
    }
}

/// <summary>
/// Shows what tax would be charged on an amount — the check a shop runs after changing a rate, and
/// the one support asks for when a receipt looks wrong.
/// </summary>
public sealed class PreviewTaxHandler : IUseCase<PreviewTaxRequest, PreviewTaxResponse>
{
    private readonly ITaxResolver _taxes;
    private readonly ITenantContext _tenant;

    public PreviewTaxHandler(ITaxResolver taxes, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(taxes);
        ArgumentNullException.ThrowIfNull(tenant);

        _taxes = taxes;
        _tenant = tenant;
    }

    public async Task<Result<PreviewTaxResponse>> HandleAsync(
        PreviewTaxRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount < 0m)
        {
            return PricingErrors.Invalid("An amount must not be negative.");
        }

        var quantity = request.Quantity <= 0m ? 1m : request.Quantity;

        var snapshot = await _taxes
            .ResolveAsync(
                request.TaxClassId,
                request.BranchId ?? _tenant.BranchId,
                request.AsOfUtc,
                cancellationToken)
            .ConfigureAwait(false);

        var line = TaxCalculator.Compute(snapshot, quantity, request.Amount);

        return new PreviewTaxResponse(
            snapshot.Treatment,
            snapshot.TaxRuleVersionId,
            snapshot.VatRate,
            snapshot.SdRate,
            line.Quantity,
            line.UnitPrice,
            line.TaxableValue,
            line.SdAmount,
            line.VatAmount,
            line.LineTotal,
            snapshot.ExemptionReason);
    }
}

/// <summary>
/// Tax class codes for the catalog's import and export, which name classes by code rather than id.
/// </summary>
public sealed class PricingLookupService : IPricingLookup
{
    private readonly IPricingRepository _repository;

    public PricingLookupService(IPricingRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetTaxClassCodesAsync(
        CancellationToken cancellationToken = default)
    {
        var classes = await _repository.GetTaxClassesAsync(true, cancellationToken).ConfigureAwait(false);

        return classes.ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Mapping between pricing entities and their contracts.</summary>
internal static class PricingMapping
{
    public static TaxClassResponse ToResponse(TaxClass taxClass, IReadOnlyList<TaxRule> rules) =>
        new(
            taxClass.Id,
            taxClass.Code,
            taxClass.Name,
            taxClass.Treatment,
            taxClass.ExemptionReason,
            taxClass.IsDefault,
            taxClass.IsActive,
            rules.Select(ToResponse).ToArray());

    public static TaxRuleResponse ToResponse(TaxRule rule) =>
        new(
            rule.Id,
            rule.Name,
            rule.BranchId,
            rule.Versions
                .OrderByDescending(v => v.Version)
                .Select(v => new TaxRuleVersionResponse(
                    v.Id,
                    v.Version,
                    v.VatRate,
                    v.SdRate,
                    v.SdInVatBase,
                    v.PriceIncludesTax,
                    v.EffectiveFromUtc,
                    v.EffectiveToUtc,
                    v.SroReference,
                    v.Notes))
                .ToArray());

    public static PriceListResponse ToResponse(PriceList list) =>
        new(
            list.Id,
            list.Code,
            list.Name,
            list.CustomerGroupId,
            list.BranchId,
            list.Priority,
            list.EffectiveFromUtc,
            list.EffectiveToUtc,
            list.IsDefault,
            list.IsActive,
            list.Items.Count);

    public static BranchPriceResponse ToResponse(BranchPriceOverride price) =>
        new(
            price.BranchId,
            price.ProductVariantId,
            price.UnitPrice,
            price.EffectiveFromUtc,
            price.EffectiveToUtc,
            price.IsActive);

    public static DiscountRuleResponse ToResponse(DiscountRule rule) =>
        new(
            rule.Id,
            rule.Code,
            rule.Name,
            rule.Scope,
            rule.Kind,
            rule.Value,
            rule.TargetId,
            rule.BranchId,
            rule.CustomerGroupId,
            rule.MinQuantity,
            rule.MinBasketValue,
            rule.MaxDiscountAmount,
            rule.EffectiveFromUtc,
            rule.EffectiveToUtc,
            rule.Priority,
            rule.IsStackable,
            rule.RequiredPermission,
            rule.IsActive);
}

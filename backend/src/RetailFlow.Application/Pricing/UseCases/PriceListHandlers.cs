using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.UseCases;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Pricing.Abstractions;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Pricing.UseCases;

/// <summary>Lists price lists.</summary>
public sealed class ListPriceListsHandler : IUseCase<ListPriceListsRequest, IReadOnlyList<PriceListResponse>>
{
    private readonly IPricingRepository _repository;

    public ListPriceListsHandler(IPricingRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<PriceListResponse>>> HandleAsync(
        ListPriceListsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lists = await _repository
            .GetPriceListsAsync(request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        return lists
            .OrderByDescending(l => l.IsDefault)
            .ThenByDescending(l => l.Priority)
            .ThenBy(l => l.Code, StringComparer.Ordinal)
            .Select(PricingMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>Creates a price list, optionally scoped to a customer group or a branch.</summary>
public sealed class CreatePriceListHandler : IUseCase<CreatePriceListRequest, PriceListResponse>
{
    private readonly IPricingRepository _repository;
    private readonly ITenancyRepository _tenancy;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public CreatePriceListHandler(
        IPricingRepository repository,
        ITenancyRepository tenancy,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _tenancy = tenancy;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<PriceListResponse>> HandleAsync(
        CreatePriceListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.PriceListCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return PricingErrors.CodeTaken("price_list", code);
        }

        if (request.BranchId is { } branchId
            && await _tenancy.FindBranchAsync(branchId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("tenancy.branch_not_found", "That branch does not exist.");
        }

        PriceList list;

        try
        {
            list = PriceList.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                code,
                request.Name,
                request.Priority,
                request.CustomerGroupId,
                request.BranchId,
                request.MakeDefault);

            list.SetEffectiveWindow(request.EffectiveFromUtc, request.EffectiveToUtc);
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        if (request.MakeDefault)
        {
            // Exactly one default list, so the old one steps aside in the same transaction.
            var current = await _repository.FindDefaultPriceListAsync(cancellationToken).ConfigureAwait(false);
            current?.ClearDefault();
        }

        _repository.AddPriceList(list);

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.price_list_created",
                nameof(PriceList),
                list.Id,
                After: new { list.Code, list.Name, list.Priority, list.IsDefault, list.CustomerGroupId, list.BranchId }),
            cancellationToken).ConfigureAwait(false);

        return PricingMapping.ToResponse(list);
    }
}

/// <summary>
/// Sets prices in a list. Takes a batch, because pricing is done a category at a time and one call
/// per variant would make a 500-line price update 500 transactions.
/// </summary>
public sealed class SetPriceListItemsHandler : IUseCase<SetPriceListItemsRequest, SetPriceListItemsResponse>
{
    private const int MaxItems = 1_000;

    private readonly IPricingRepository _repository;
    private readonly ICatalogRepository _catalog;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public SetPriceListItemsHandler(
        IPricingRepository repository,
        ICatalogRepository catalog,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _catalog = catalog;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<SetPriceListItemsResponse>> HandleAsync(
        SetPriceListItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Items is null || request.Items.Count == 0)
        {
            return PricingErrors.Invalid("Give at least one price to set.");
        }

        if (request.Items.Count > MaxItems)
        {
            return PricingErrors.Invalid($"Set at most {MaxItems} prices per call.");
        }

        var list = await _repository
            .FindPriceListAsync(request.PriceListId, cancellationToken)
            .ConfigureAwait(false);

        if (list is null)
        {
            return PricingErrors.PriceListNotFound();
        }

        // Every variant is checked before anything is written: a batch that prices four variants
        // and one typo should change nothing, not four fifths of a price update.
        foreach (var item in request.Items)
        {
            if (await _catalog.FindVariantAsync(item.VariantId, cancellationToken).ConfigureAwait(false) is null)
            {
                return CatalogErrors.VariantNotFound();
            }

            if (item.UnitPrice < 0m)
            {
                return PricingErrors.Invalid("A price must not be negative.");
            }
        }

        try
        {
            foreach (var item in request.Items)
            {
                list.SetPrice(_guids.NewId(), item.VariantId, item.UnitPrice, item.MinQuantity);
            }
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.price_list_items_set",
                nameof(PriceList),
                list.Id,
                After: new { list.Code, Items = request.Items.Count }),
            cancellationToken).ConfigureAwait(false);

        return new SetPriceListItemsResponse(list.Id, request.Items.Count);
    }
}

/// <summary>Sets or replaces a branch's price for one variant.</summary>
public sealed class SetBranchPriceHandler : IUseCase<SetBranchPriceRequest, BranchPriceResponse>
{
    private readonly IPricingRepository _repository;
    private readonly ICatalogRepository _catalog;
    private readonly ITenancyRepository _tenancy;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public SetBranchPriceHandler(
        IPricingRepository repository,
        ICatalogRepository catalog,
        ITenancyRepository tenancy,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _catalog = catalog;
        _tenancy = tenancy;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<BranchPriceResponse>> HandleAsync(
        SetBranchPriceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _tenancy.FindBranchAsync(request.BranchId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("tenancy.branch_not_found", "That branch does not exist.");
        }

        if (await _catalog.FindVariantAsync(request.VariantId, cancellationToken).ConfigureAwait(false) is null)
        {
            return CatalogErrors.VariantNotFound();
        }

        var existing = await _repository
            .FindBranchPriceAsync(request.BranchId, request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (existing is null)
            {
                existing = BranchPriceOverride.Create(
                    _guids.NewId(),
                    _tenant.OrganizationId,
                    request.BranchId,
                    request.VariantId,
                    request.UnitPrice);

                existing.SetEffectiveWindow(request.EffectiveFromUtc, request.EffectiveToUtc);
                _repository.AddBranchPrice(existing);
            }
            else
            {
                existing.ChangePrice(request.UnitPrice);
                existing.SetEffectiveWindow(request.EffectiveFromUtc, request.EffectiveToUtc);
                existing.Activate();
            }
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.branch_price_set",
                nameof(BranchPriceOverride),
                existing.Id,
                After: new { existing.BranchId, existing.ProductVariantId, existing.UnitPrice }),
            cancellationToken).ConfigureAwait(false);

        return PricingMapping.ToResponse(existing);
    }
}

/// <summary>Lists a branch's price overrides.</summary>
public sealed class ListBranchPricesHandler : IUseCase<ListBranchPricesRequest, IReadOnlyList<BranchPriceResponse>>
{
    private readonly IPricingRepository _repository;

    public ListBranchPricesHandler(IPricingRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<BranchPriceResponse>>> HandleAsync(
        ListBranchPricesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prices = await _repository
            .GetBranchPricesAsync(request.BranchId, cancellationToken)
            .ConfigureAwait(false);

        return prices.Select(PricingMapping.ToResponse).ToArray();
    }
}

/// <summary>
/// Answers "why is this ringing up at that price" by running the resolution order and naming the
/// source that won (docs/02 §1).
/// </summary>
public sealed class ResolvePriceHandler : IUseCase<ResolvePriceRequest, ResolvePriceResponse>
{
    private readonly IPriceResolver _resolver;
    private readonly ICatalogRepository _catalog;
    private readonly ITenantContext _tenant;

    public ResolvePriceHandler(IPriceResolver resolver, ICatalogRepository catalog, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tenant);

        _resolver = resolver;
        _catalog = catalog;
        _tenant = tenant;
    }

    public async Task<Result<ResolvePriceResponse>> HandleAsync(
        ResolvePriceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var variant = await _catalog
            .FindVariantAsync(request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        if (variant is null)
        {
            return CatalogErrors.VariantNotFound();
        }

        var quantity = request.Quantity <= 0m ? 1m : request.Quantity;

        var resolved = await _resolver
            .ResolveAsync(
                new PriceQuery(
                    request.VariantId,
                    quantity,
                    request.BranchId ?? _tenant.BranchId,
                    request.CustomerGroupId),
                cancellationToken)
            .ConfigureAwait(false);

        return new ResolvePriceResponse(
            request.VariantId,
            quantity,
            resolved.UnitPrice,
            resolved.Source,
            resolved.SourceId,
            resolved.MinQuantity,
            variant.BasePrice);
    }
}

/// <summary>Lists standing discount rules.</summary>
public sealed class ListDiscountRulesHandler
    : IUseCase<ListDiscountRulesRequest, IReadOnlyList<DiscountRuleResponse>>
{
    private readonly IPricingRepository _repository;

    public ListDiscountRulesHandler(IPricingRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<DiscountRuleResponse>>> HandleAsync(
        ListDiscountRulesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rules = await _repository
            .GetDiscountRulesAsync(request.IncludeInactive, cancellationToken)
            .ConfigureAwait(false);

        return rules
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .Select(PricingMapping.ToResponse)
            .ToArray();
    }
}

/// <summary>Creates a standing discount rule.</summary>
public sealed class CreateDiscountRuleHandler : IUseCase<CreateDiscountRuleRequest, DiscountRuleResponse>
{
    private readonly IPricingRepository _repository;
    private readonly ICatalogRepository _catalog;
    private readonly ITenantContext _tenant;
    private readonly IAuditWriter _audit;
    private readonly IGuidGenerator _guids;

    public CreateDiscountRuleHandler(
        IPricingRepository repository,
        ICatalogRepository catalog,
        ITenantContext tenant,
        IAuditWriter audit,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(guids);

        _repository = repository;
        _catalog = catalog;
        _tenant = tenant;
        _audit = audit;
        _guids = guids;
    }

    public async Task<Result<DiscountRuleResponse>> HandleAsync(
        CreateDiscountRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (await _repository.DiscountCodeExistsAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            return PricingErrors.CodeTaken("discount", code);
        }

        var targetCheck = await CheckTargetAsync(request, cancellationToken).ConfigureAwait(false);

        if (targetCheck is not null)
        {
            return targetCheck;
        }

        DiscountRule rule;

        try
        {
            rule = DiscountRule.Create(
                _guids.NewId(),
                _tenant.OrganizationId,
                code,
                request.Name,
                request.Scope,
                request.Kind,
                request.Value,
                request.TargetId);

            rule.SetConditions(
                request.BranchId,
                request.CustomerGroupId,
                request.MinQuantity,
                request.MinBasketValue,
                request.MaxDiscountAmount);

            rule.SetEffectiveWindow(request.EffectiveFromUtc, request.EffectiveToUtc);
            rule.SetPolicy(request.Priority, request.IsStackable, request.RequiredPermission);
        }
        catch (DomainException ex)
        {
            return PricingErrors.Invalid(ex.Message);
        }

        _repository.AddDiscountRule(rule);

        await _audit.WriteAsync(
            new AuditEntry(
                "pricing.discount_rule_created",
                nameof(DiscountRule),
                rule.Id,
                After: new { rule.Code, rule.Scope, rule.Kind, rule.Value, rule.TargetId }),
            cancellationToken).ConfigureAwait(false);

        return PricingMapping.ToResponse(rule);
    }

    private async Task<Error?> CheckTargetAsync(
        CreateDiscountRuleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetId is not { } targetId)
        {
            return null;
        }

        return request.Scope switch
        {
            DiscountScope.Variant when await _catalog
                .FindVariantAsync(targetId, cancellationToken).ConfigureAwait(false) is null
                => CatalogErrors.VariantNotFound(),

            DiscountScope.Category when await _catalog
                .FindCategoryAsync(targetId, cancellationToken).ConfigureAwait(false) is null
                => CatalogErrors.CategoryNotFound(),

            DiscountScope.Brand when await _catalog
                .FindBrandAsync(targetId, cancellationToken).ConfigureAwait(false) is null
                => CatalogErrors.BrandNotFound(),

            _ => null,
        };
    }
}

/// <summary>Activates or withdraws a discount rule.</summary>
public sealed class SetDiscountRuleActivationHandler
    : IUseCase<SetDiscountRuleActivationRequest, DiscountRuleResponse>
{
    private readonly IPricingRepository _repository;
    private readonly IAuditWriter _audit;

    public SetDiscountRuleActivationHandler(IPricingRepository repository, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(audit);

        _repository = repository;
        _audit = audit;
    }

    public async Task<Result<DiscountRuleResponse>> HandleAsync(
        SetDiscountRuleActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await _repository
            .FindDiscountRuleAsync(request.DiscountRuleId, cancellationToken)
            .ConfigureAwait(false);

        if (rule is null)
        {
            return PricingErrors.DiscountRuleNotFound();
        }

        if (request.IsActive)
        {
            rule.Activate();
        }
        else
        {
            rule.Deactivate();
        }

        await _audit.WriteAsync(
            new AuditEntry(
                request.IsActive ? "pricing.discount_rule_activated" : "pricing.discount_rule_deactivated",
                nameof(DiscountRule),
                rule.Id,
                After: new { rule.Code, rule.IsActive }),
            cancellationToken).ConfigureAwait(false);

        return PricingMapping.ToResponse(rule);
    }
}

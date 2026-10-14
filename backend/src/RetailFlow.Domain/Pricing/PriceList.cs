using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Pricing;

/// <summary>
/// A named set of prices that applies to a customer group, a branch, or the whole tenant for a
/// period (FR-PRC-02). Several may apply at once; <see cref="Priority"/> decides, and the
/// resolution order in <c>docs/02-business-rules.md</c> §1 decides against the other sources.
/// </summary>
public sealed class PriceList : TenantEntity
{
    private readonly List<PriceListItem> _items = [];

    private PriceList()
    {
    }

    private PriceList(Guid id, Guid organizationId, string code, string name, int priority)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        Priority = priority;
        IsActive = true;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Null for a list that applies to every customer; set for a group's list.</summary>
    public Guid? CustomerGroupId { get; private set; }

    /// <summary>Null for a list that applies at every branch.</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>Higher wins when more than one list applies.</summary>
    public int Priority { get; private set; }

    public DateTimeOffset? EffectiveFromUtc { get; private set; }

    public DateTimeOffset? EffectiveToUtc { get; private set; }

    /// <summary>
    /// The tenant's base list. Exactly one, and it is the list the resolution order falls back to
    /// before the variant's own base price.
    /// </summary>
    public bool IsDefault { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<PriceListItem> Items => _items;

    public static PriceList Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        int priority = 0,
        Guid? customerGroupId = null,
        Guid? branchId = null,
        bool isDefault = false)
    {
        var list = new PriceList(
            id,
            organizationId,
            PricingText.Code(code, nameof(code)),
            PricingText.Required(name, nameof(name), 100),
            priority)
        {
            CustomerGroupId = customerGroupId,
            BranchId = branchId,
            IsDefault = isDefault,
        };

        if (isDefault && (customerGroupId is not null || branchId is not null))
        {
            throw new DomainException(
                "The default price list applies to the whole tenant, so it cannot be scoped to a group or branch.");
        }

        return list;
    }

    public void Rename(string name) => Name = PricingText.Required(name, nameof(name), 100);

    public void SetScope(Guid? customerGroupId, Guid? branchId, int priority)
    {
        if (IsDefault && (customerGroupId is not null || branchId is not null))
        {
            throw new DomainException("The default price list cannot be scoped to a group or branch.");
        }

        CustomerGroupId = customerGroupId;
        BranchId = branchId;
        Priority = priority;
    }

    public void SetEffectiveWindow(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc is { } from && toUtc is { } to && to <= from)
        {
            throw new DomainException("A price list cannot end before it starts.");
        }

        EffectiveFromUtc = fromUtc;
        EffectiveToUtc = toUtc;
    }

    /// <summary>
    /// Sets a price for a variant, optionally only from a minimum quantity — which is how
    /// wholesale tiers are expressed ("20 BDT each, 18 from a dozen").
    /// </summary>
    public PriceListItem SetPrice(Guid itemId, Guid productVariantId, decimal unitPrice, decimal minQuantity = 0m)
    {
        if (unitPrice < 0m)
        {
            throw new DomainException("A price must not be negative.");
        }

        if (minQuantity < 0m)
        {
            throw new DomainException("A minimum quantity must not be negative.");
        }

        var normalizedMin = RoundingPolicy.RoundQuantity(minQuantity);

        var existing = _items.FirstOrDefault(i =>
            i.ProductVariantId == productVariantId && i.MinQuantity == normalizedMin);

        if (existing is not null)
        {
            existing.ChangePrice(unitPrice);
            return existing;
        }

        var item = PriceListItem.Create(itemId, OrganizationId, Id, productVariantId, unitPrice, normalizedMin);
        _items.Add(item);
        return item;
    }

    public bool IsEffectiveAt(DateTimeOffset atUtc) =>
        IsActive
        && (EffectiveFromUtc is null || EffectiveFromUtc <= atUtc)
        && (EffectiveToUtc is null || atUtc < EffectiveToUtc);

    public void Activate() => IsActive = true;

    public void Deactivate()
    {
        if (IsDefault)
        {
            throw new DomainException("The default price list cannot be deactivated.");
        }

        IsActive = false;
    }

    public void MakeDefault()
    {
        if (CustomerGroupId is not null || BranchId is not null)
        {
            throw new DomainException("A scoped price list cannot be the tenant default.");
        }

        IsDefault = true;
    }

    public void ClearDefault() => IsDefault = false;
}

/// <summary>One variant's price in a list, optionally from a minimum quantity upwards.</summary>
public sealed class PriceListItem : TenantEntity
{
    private PriceListItem()
    {
    }

    private PriceListItem(
        Guid id,
        Guid organizationId,
        Guid priceListId,
        Guid productVariantId,
        decimal unitPrice,
        decimal minQuantity)
        : base(id, organizationId)
    {
        PriceListId = priceListId;
        ProductVariantId = productVariantId;
        UnitPrice = unitPrice;
        MinQuantity = minQuantity;
    }

    public Guid PriceListId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public decimal UnitPrice { get; private set; }

    /// <summary>Quantity from which this price applies. Zero means "any quantity".</summary>
    public decimal MinQuantity { get; private set; }

    internal static PriceListItem Create(
        Guid id,
        Guid organizationId,
        Guid priceListId,
        Guid productVariantId,
        decimal unitPrice,
        decimal minQuantity) =>
        new(id, organizationId, priceListId, productVariantId, RoundingPolicy.Round4(unitPrice), minQuantity);

    internal void ChangePrice(decimal unitPrice)
    {
        if (unitPrice < 0m)
        {
            throw new DomainException("A price must not be negative.");
        }

        UnitPrice = RoundingPolicy.Round4(unitPrice);
    }
}

/// <summary>
/// A price for one variant at one branch — the simple case that would otherwise need a whole price
/// list per shop, which is how a five-branch tenant ends up with five lists nobody maintains.
/// </summary>
public sealed class BranchPriceOverride : TenantEntity
{
    private BranchPriceOverride()
    {
    }

    private BranchPriceOverride(
        Guid id,
        Guid organizationId,
        Guid branchId,
        Guid productVariantId,
        decimal unitPrice)
        : base(id, organizationId)
    {
        BranchId = branchId;
        ProductVariantId = productVariantId;
        UnitPrice = unitPrice;
        IsActive = true;
    }

    public Guid BranchId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public decimal UnitPrice { get; private set; }

    public DateTimeOffset? EffectiveFromUtc { get; private set; }

    public DateTimeOffset? EffectiveToUtc { get; private set; }

    public bool IsActive { get; private set; }

    public static BranchPriceOverride Create(
        Guid id,
        Guid organizationId,
        Guid branchId,
        Guid productVariantId,
        decimal unitPrice)
    {
        if (unitPrice < 0m)
        {
            throw new DomainException("A price must not be negative.");
        }

        return new BranchPriceOverride(
            id, organizationId, branchId, productVariantId, RoundingPolicy.Round4(unitPrice));
    }

    public void ChangePrice(decimal unitPrice)
    {
        if (unitPrice < 0m)
        {
            throw new DomainException("A price must not be negative.");
        }

        UnitPrice = RoundingPolicy.Round4(unitPrice);
    }

    public void SetEffectiveWindow(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc is { } from && toUtc is { } to && to <= from)
        {
            throw new DomainException("A price override cannot end before it starts.");
        }

        EffectiveFromUtc = fromUtc;
        EffectiveToUtc = toUtc;
    }

    public bool IsEffectiveAt(DateTimeOffset atUtc) =>
        IsActive
        && (EffectiveFromUtc is null || EffectiveFromUtc <= atUtc)
        && (EffectiveToUtc is null || atUtc < EffectiveToUtc);

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}

/// <summary>What a discount rule applies to.</summary>
public enum DiscountScope
{
    /// <summary>One variant.</summary>
    Variant = 0,

    /// <summary>Every product in a category.</summary>
    Category = 1,

    /// <summary>Every product of a brand.</summary>
    Brand = 2,

    /// <summary>The whole bill.</summary>
    Bill = 3,
}

/// <summary>Whether a discount is a percentage or a fixed amount.</summary>
public enum DiscountKind
{
    Percent = 0,
    Amount = 1,
}

/// <summary>
/// A standing discount a cashier does not have to key in — "10% off dairy this week", "50 BDT off
/// a 1 000 BDT basket". Separate from a cashier's ad-hoc line discount, which is authorised by
/// permission and recorded on the sale (docs/02 §1).
/// </summary>
public sealed class DiscountRule : TenantEntity
{
    private DiscountRule()
    {
    }

    private DiscountRule(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        DiscountScope scope,
        DiscountKind kind,
        decimal value)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        Scope = scope;
        Kind = kind;
        Value = value;
        IsActive = true;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public DiscountScope Scope { get; private set; }

    public DiscountKind Kind { get; private set; }

    /// <summary>Percentage (0–100) or a fixed amount, per <see cref="Kind"/>.</summary>
    public decimal Value { get; private set; }

    /// <summary>The target of the scope: the variant, category or brand id. Null for a bill rule.</summary>
    public Guid? TargetId { get; private set; }

    /// <summary>Restricts the rule to one branch.</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>Restricts the rule to one customer group.</summary>
    public Guid? CustomerGroupId { get; private set; }

    /// <summary>Smallest qualifying quantity (line scope) or basket value (bill scope).</summary>
    public decimal? MinQuantity { get; private set; }

    public decimal? MinBasketValue { get; private set; }

    /// <summary>Caps a percentage discount in money terms.</summary>
    public decimal? MaxDiscountAmount { get; private set; }

    public DateTimeOffset? EffectiveFromUtc { get; private set; }

    public DateTimeOffset? EffectiveToUtc { get; private set; }

    public int Priority { get; private set; }

    /// <summary>Whether this rule may combine with another that also matches.</summary>
    public bool IsStackable { get; private set; }

    /// <summary>A permission the cashier must hold for the rule to apply at the till.</summary>
    public string? RequiredPermission { get; private set; }

    public bool IsActive { get; private set; }

    public static DiscountRule Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        DiscountScope scope,
        DiscountKind kind,
        decimal value,
        Guid? targetId = null)
    {
        ValidateValue(kind, value);

        if (scope is DiscountScope.Variant or DiscountScope.Category or DiscountScope.Brand && targetId is null)
        {
            throw new DomainException($"A {scope} discount needs the {scope} it applies to.");
        }

        if (scope == DiscountScope.Bill && targetId is not null)
        {
            throw new DomainException("A bill discount applies to the whole sale, so it takes no target.");
        }

        return new DiscountRule(
            id,
            organizationId,
            PricingText.Code(code, nameof(code)),
            PricingText.Required(name, nameof(name), 100),
            scope,
            kind,
            kind == DiscountKind.Percent ? RoundingPolicy.Round6(value) : RoundingPolicy.Round4(value))
        {
            TargetId = targetId,
        };
    }

    public void SetValue(DiscountKind kind, decimal value)
    {
        ValidateValue(kind, value);

        Kind = kind;
        Value = kind == DiscountKind.Percent ? RoundingPolicy.Round6(value) : RoundingPolicy.Round4(value);
    }

    public void SetConditions(
        Guid? branchId,
        Guid? customerGroupId,
        decimal? minQuantity,
        decimal? minBasketValue,
        decimal? maxDiscountAmount)
    {
        if (minQuantity is { } quantity && quantity < 0m)
        {
            throw new DomainException("A minimum quantity must not be negative.");
        }

        if (minBasketValue is { } basket && basket < 0m)
        {
            throw new DomainException("A minimum basket value must not be negative.");
        }

        if (maxDiscountAmount is { } max && max < 0m)
        {
            throw new DomainException("A maximum discount must not be negative.");
        }

        BranchId = branchId;
        CustomerGroupId = customerGroupId;
        MinQuantity = minQuantity is { } q ? RoundingPolicy.RoundQuantity(q) : null;
        MinBasketValue = minBasketValue is { } b ? RoundingPolicy.Round4(b) : null;
        MaxDiscountAmount = maxDiscountAmount is { } m ? RoundingPolicy.Round4(m) : null;
    }

    public void SetEffectiveWindow(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc is { } from && toUtc is { } to && to <= from)
        {
            throw new DomainException("A discount rule cannot end before it starts.");
        }

        EffectiveFromUtc = fromUtc;
        EffectiveToUtc = toUtc;
    }

    public void SetPolicy(int priority, bool isStackable, string? requiredPermission)
    {
        Priority = priority;
        IsStackable = isStackable;
        RequiredPermission = PricingText.Optional(requiredPermission, 64);
    }

    public bool IsEffectiveAt(DateTimeOffset atUtc) =>
        IsActive
        && (EffectiveFromUtc is null || EffectiveFromUtc <= atUtc)
        && (EffectiveToUtc is null || atUtc < EffectiveToUtc);

    /// <summary>
    /// The discount this rule produces on a line value, capped by
    /// <see cref="MaxDiscountAmount"/> and never more than the value itself.
    /// </summary>
    public decimal AmountFor(decimal lineValue)
    {
        if (lineValue <= 0m)
        {
            return 0m;
        }

        var raw = Kind == DiscountKind.Percent
            ? RoundingPolicy.Round4(lineValue * (Value / 100m))
            : Value;

        if (MaxDiscountAmount is { } cap && raw > cap)
        {
            raw = cap;
        }

        return Math.Min(RoundingPolicy.Round4(raw), lineValue);
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    private static void ValidateValue(DiscountKind kind, decimal value)
    {
        if (value < 0m)
        {
            throw new DomainException("A discount value must not be negative.");
        }

        if (kind == DiscountKind.Percent && value > 100m)
        {
            throw new DomainException("A percentage discount cannot exceed 100.");
        }
    }
}

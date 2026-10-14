using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Catalog;

/// <summary>What kind of thing is being sold.</summary>
public enum ProductType
{
    /// <summary>A physical item that moves through the stock ledger.</summary>
    Standard = 0,

    /// <summary>Labour or a fee: sellable, never stocked (delivery, tailoring, repair).</summary>
    Service = 1,
}

/// <summary>
/// A sellable thing. Stock, price and barcodes hang off its variants, not off the product: even a
/// shop that thinks it has no variants has one — the default — and modelling that uniformly is what
/// stops "add a 500 ml size" from becoming a schema change (FR-CAT-02).
/// </summary>
public sealed class Product : TenantEntity
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductAttribute> _attributes = [];

    private Product()
    {
    }

    private Product(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        Guid baseUomId,
        ProductType productType)
        : base(id, organizationId)
    {
        if (baseUomId == Guid.Empty)
        {
            throw new ArgumentException("A product needs a base unit of measure.", nameof(baseUomId));
        }

        Code = code;
        Name = name;
        BaseUomId = baseUomId;
        ProductType = productType;
        TrackInventory = productType == ProductType.Standard;
        Status = CatalogStatus.Draft;
    }

    /// <summary>Internal code, unique per tenant. Printed on labels and used in import files.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string? Description { get; private set; }

    public Guid? CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    /// <summary>Tax treatment. Null means the organization's default class applies.</summary>
    public Guid? TaxClassId { get; private set; }

    /// <summary>Unit the product's stock is kept in; variants may sell in a multiple of it.</summary>
    public Guid BaseUomId { get; private set; }

    public ProductType ProductType { get; private set; }

    /// <summary>Whether sales and receipts move the stock ledger. Always false for a service.</summary>
    public bool TrackInventory { get; private set; }

    /// <summary>Stock is identified by batch/lot — required for anything with an expiry.</summary>
    public bool TrackBatch { get; private set; }

    /// <summary>Each unit carries a serial number (electronics, warranty claims).</summary>
    public bool TrackSerial { get; private set; }

    /// <summary>Stock carries an expiry date, which the POS checks before selling.</summary>
    public bool TrackExpiry { get; private set; }

    /// <summary>
    /// Days before expiry at which this product should stop being sold without the
    /// <c>inventory.sell_near_expiry</c> permission. Null means the branch default applies.
    /// </summary>
    public int? NearExpiryDays { get; private set; }

    public CatalogStatus Status { get; private set; }

    public IReadOnlyCollection<ProductVariant> Variants => _variants;

    public IReadOnlyCollection<ProductAttribute> Attributes => _attributes;

    public static Product Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        Guid baseUomId,
        ProductType productType = ProductType.Standard) =>
        new(
            id,
            organizationId,
            CatalogText.Code(code, nameof(code), 64),
            CatalogText.Required(name, nameof(name), 300),
            baseUomId,
            productType);

    public void UpdateDetails(string name, string? nameBn, string? description)
    {
        EnsureNotArchived();

        Name = CatalogText.Required(name, nameof(name), 300);
        NameBn = CatalogText.Optional(nameBn, 300);
        Description = CatalogText.Optional(description, 2000);
    }

    public void Classify(Guid? categoryId, Guid? brandId, Guid? taxClassId)
    {
        EnsureNotArchived();

        CategoryId = categoryId;
        BrandId = brandId;
        TaxClassId = taxClassId;
    }

    /// <summary>
    /// Sets the stock-tracking policy. The combinations are constrained rather than free: expiry
    /// without batches has nothing to attach a date to, and a serialised item cannot be sold in
    /// fractions, so the two policies that would produce unreconcilable stock are refused here
    /// instead of failing later in the ledger.
    /// </summary>
    public void SetTrackingPolicy(
        bool trackInventory,
        bool trackBatch,
        bool trackSerial,
        bool trackExpiry,
        int? nearExpiryDays = null)
    {
        EnsureNotArchived();

        if (ProductType == ProductType.Service)
        {
            if (trackInventory || trackBatch || trackSerial || trackExpiry)
            {
                throw new DomainException("A service does not hold stock, so it cannot be tracked.");
            }

            TrackInventory = TrackBatch = TrackSerial = TrackExpiry = false;
            NearExpiryDays = null;
            return;
        }

        if (!trackInventory && (trackBatch || trackSerial || trackExpiry))
        {
            throw new DomainException("Batch, serial and expiry tracking need inventory tracking turned on.");
        }

        if (trackExpiry && !trackBatch)
        {
            throw new DomainException("Expiry tracking needs batch tracking: an expiry date belongs to a batch.");
        }

        if (trackSerial && trackBatch)
        {
            throw new DomainException(
                "Track either serials or batches, not both: a serialised unit is its own batch of one.");
        }

        if (nearExpiryDays is { } days && days < 0)
        {
            throw new DomainException("Near-expiry days must not be negative.");
        }

        TrackInventory = true;
        TrackBatch = trackBatch;
        TrackSerial = trackSerial;
        TrackExpiry = trackExpiry;
        NearExpiryDays = trackExpiry ? nearExpiryDays : null;
    }

    /// <summary>
    /// Adds a variant. The first one becomes the default, because every product must have exactly
    /// one variant the POS can reach for when a cashier scans the product rather than a size.
    /// </summary>
    public ProductVariant AddVariant(
        Guid variantId,
        string sku,
        string? name,
        decimal basePrice,
        Guid uomId,
        decimal packSize = 1m)
    {
        EnsureNotArchived();

        var variant = ProductVariant.Create(
            variantId,
            OrganizationId,
            Id,
            sku,
            name,
            basePrice,
            uomId,
            packSize,
            isDefault: _variants.Count == 0);

        _variants.Add(variant);
        return variant;
    }

    /// <summary>Moves the default flag to one of this product's variants.</summary>
    public void SetDefaultVariant(Guid variantId)
    {
        var target = _variants.FirstOrDefault(v => v.Id == variantId)
            ?? throw new DomainException("That variant does not belong to this product.");

        if (target.Status == CatalogStatus.Archived)
        {
            throw new DomainException("An archived variant cannot be the default.");
        }

        foreach (var variant in _variants)
        {
            variant.ClearDefault();
        }

        target.MakeDefault();
    }

    public ProductAttribute SetAttribute(Guid attributeId, string name, string value, int sortOrder = 0)
    {
        EnsureNotArchived();

        var normalizedName = CatalogText.Required(name, nameof(name), 64);

        var existing = _attributes.FirstOrDefault(a =>
            string.Equals(a.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.ChangeValue(value);
            return existing;
        }

        var attribute = ProductAttribute.Create(attributeId, OrganizationId, Id, normalizedName, value, sortOrder);
        _attributes.Add(attribute);
        return attribute;
    }

    /// <summary>
    /// Makes the product sellable. Refused without a variant: a product with no variant has no
    /// price and no barcode, so "active" would be a lie the POS discovers at the till.
    /// </summary>
    public void Activate()
    {
        if (Status == CatalogStatus.Archived)
        {
            throw new DomainException("An archived product cannot be reactivated. Create a new one.");
        }

        if (_variants.Count == 0)
        {
            throw new DomainException("Add at least one variant before activating the product.");
        }

        if (_variants.All(v => v.Status != CatalogStatus.Active))
        {
            throw new DomainException("At least one variant must be active.");
        }

        Status = CatalogStatus.Active;
    }

    public void Deactivate() => Status = CatalogStatus.Inactive;

    /// <summary>
    /// Withdraws the product for good, archiving its variants with it. Nothing is deleted, because
    /// sale lines, stock movements and tax filings reference it (NFR-09).
    /// </summary>
    public void Archive()
    {
        Status = CatalogStatus.Archived;

        foreach (var variant in _variants)
        {
            variant.Archive();
        }
    }

    public bool IsSellable => Status == CatalogStatus.Active;

    private void EnsureNotArchived()
    {
        if (Status == CatalogStatus.Archived)
        {
            throw new DomainException("An archived product cannot be changed.");
        }
    }
}

/// <summary>A named characteristic of a product, shown on the product page and in search.</summary>
public sealed class ProductAttribute : TenantEntity
{
    private ProductAttribute()
    {
    }

    private ProductAttribute(Guid id, Guid organizationId, Guid productId, string name, string value, int sortOrder)
        : base(id, organizationId)
    {
        ProductId = productId;
        Name = name;
        Value = value;
        SortOrder = sortOrder;
    }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    internal static ProductAttribute Create(
        Guid id,
        Guid organizationId,
        Guid productId,
        string name,
        string value,
        int sortOrder) =>
        new(id, organizationId, productId, name, CatalogText.Required(value, nameof(value), 256), sortOrder);

    internal void ChangeValue(string value) => Value = CatalogText.Required(value, nameof(value), 256);
}

/// <summary>
/// Per-branch stock policy for a product: what to keep on the shelf and whether this branch sells
/// it at all. Thresholds are per branch because a city-centre shop and a suburban one carry
/// different depth of the same item (FR-CAT-08).
/// </summary>
public sealed class ProductBranchSetting : TenantEntity
{
    private ProductBranchSetting()
    {
    }

    private ProductBranchSetting(Guid id, Guid organizationId, Guid productVariantId, Guid branchId)
        : base(id, organizationId)
    {
        ProductVariantId = productVariantId;
        BranchId = branchId;
        IsSellable = true;
    }

    public Guid ProductVariantId { get; private set; }

    public Guid BranchId { get; private set; }

    /// <summary>Stock level below which the branch is considered short.</summary>
    public decimal? MinStock { get; private set; }

    /// <summary>Level at which a reorder is suggested; must not be below <see cref="MinStock"/>.</summary>
    public decimal? ReorderLevel { get; private set; }

    /// <summary>How much to order when reordering.</summary>
    public decimal? ReorderQuantity { get; private set; }

    public bool IsSellable { get; private set; }

    public static ProductBranchSetting Create(
        Guid id,
        Guid organizationId,
        Guid productVariantId,
        Guid branchId) =>
        new(id, organizationId, productVariantId, branchId);

    public void SetThresholds(decimal? minStock, decimal? reorderLevel, decimal? reorderQuantity)
    {
        foreach (var (value, name) in new[]
        {
            (minStock, nameof(minStock)),
            (reorderLevel, nameof(reorderLevel)),
            (reorderQuantity, nameof(reorderQuantity)),
        })
        {
            if (value is { } quantity && quantity < 0m)
            {
                throw new DomainException($"{name} must not be negative.");
            }
        }

        if (minStock is { } min && reorderLevel is { } level && level < min)
        {
            throw new DomainException(
                "The reorder level must be at or above the minimum stock, or the alert fires too late to act on.");
        }

        MinStock = minStock is { } m ? RoundingPolicy.RoundQuantity(m) : null;
        ReorderLevel = reorderLevel is { } r ? RoundingPolicy.RoundQuantity(r) : null;
        ReorderQuantity = reorderQuantity is { } q ? RoundingPolicy.RoundQuantity(q) : null;
    }

    public void SetSellable(bool isSellable) => IsSellable = isSellable;
}

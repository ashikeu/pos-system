using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Catalog;

/// <summary>
/// The thing that is actually priced, scanned and stocked (FR-CAT-02). A product with one size still
/// has one variant, so nothing downstream — price lists, stock balances, sale lines — needs two
/// shapes for "with variants" and "without".
/// </summary>
public sealed class ProductVariant : TenantEntity
{
    private readonly List<Barcode> _barcodes = [];

    private ProductVariant()
    {
    }

    private ProductVariant(
        Guid id,
        Guid organizationId,
        Guid productId,
        string sku,
        string? name,
        decimal basePrice,
        Guid uomId,
        decimal packSize,
        bool isDefault)
        : base(id, organizationId)
    {
        ProductId = productId;
        Sku = sku;
        Name = name;
        BasePrice = basePrice;
        UomId = uomId;
        PackSize = packSize;
        IsDefault = isDefault;
        Status = CatalogStatus.Active;
    }

    public Guid ProductId { get; private set; }

    /// <summary>Stock-keeping unit, unique per tenant. What a stock count sheet is keyed by.</summary>
    public string Sku { get; private set; } = string.Empty;

    /// <summary>Variant label such as "500 ml" or "Red / XL". Null for a single-variant product.</summary>
    public string? Name { get; private set; }

    /// <summary>
    /// Price before any price list, branch override or discount — the bottom of the resolution
    /// order and the only price that is always present.
    /// </summary>
    public decimal BasePrice { get; private set; }

    /// <summary>
    /// Most recent purchase cost, kept for margin display and for the below-cost check. The
    /// authoritative moving average lives on the stock balance (ADR-0009), not here.
    /// </summary>
    public decimal? LastPurchaseCost { get; private set; }

    /// <summary>Unit this variant is sold in.</summary>
    public Guid UomId { get; private set; }

    /// <summary>
    /// How many base units one of this variant contains — a 24-piece case has <c>PackSize = 24</c>.
    /// Stock moves in base units, so this is what converts a sale into a ledger quantity.
    /// </summary>
    public decimal PackSize { get; private set; }

    public decimal? WeightInGrams { get; private set; }

    /// <summary>The variant a bare product scan or a product-level sale resolves to.</summary>
    public bool IsDefault { get; private set; }

    public CatalogStatus Status { get; private set; }

    public IReadOnlyCollection<Barcode> Barcodes => _barcodes;

    internal static ProductVariant Create(
        Guid id,
        Guid organizationId,
        Guid productId,
        string sku,
        string? name,
        decimal basePrice,
        Guid uomId,
        decimal packSize,
        bool isDefault)
    {
        if (uomId == Guid.Empty)
        {
            throw new ArgumentException("A variant needs a unit of measure.", nameof(uomId));
        }

        if (basePrice < 0m)
        {
            throw new DomainException("A base price must not be negative.");
        }

        if (packSize <= 0m)
        {
            throw new DomainException("A pack size must be greater than zero.");
        }

        return new ProductVariant(
            id,
            organizationId,
            productId,
            CatalogText.Code(sku, nameof(sku), 64),
            CatalogText.Optional(name, 200),
            RoundingPolicy.Round4(basePrice),
            uomId,
            RoundingPolicy.RoundQuantity(packSize),
            isDefault);
    }

    public void UpdateDetails(string? name, Guid uomId, decimal packSize, decimal? weightInGrams)
    {
        EnsureNotArchived();

        if (packSize <= 0m)
        {
            throw new DomainException("A pack size must be greater than zero.");
        }

        if (weightInGrams is { } weight && weight < 0m)
        {
            throw new DomainException("A weight must not be negative.");
        }

        Name = CatalogText.Optional(name, 200);
        UomId = uomId;
        PackSize = RoundingPolicy.RoundQuantity(packSize);
        WeightInGrams = weightInGrams is { } w ? RoundingPolicy.RoundQuantity(w) : null;
    }

    public void ChangeBasePrice(decimal basePrice)
    {
        EnsureNotArchived();

        if (basePrice < 0m)
        {
            throw new DomainException("A base price must not be negative.");
        }

        BasePrice = RoundingPolicy.Round4(basePrice);
    }

    /// <summary>Records what the last receipt paid for this variant, for margin display.</summary>
    public void RecordPurchaseCost(decimal unitCost)
    {
        if (unitCost < 0m)
        {
            throw new DomainException("A purchase cost must not be negative.");
        }

        LastPurchaseCost = RoundingPolicy.Round6(unitCost);
    }

    /// <summary>
    /// Adds a barcode. The first one is primary; the rest are alternates, which is how a case
    /// barcode and a piece barcode can both reach the same variant (FR-CAT-03).
    /// </summary>
    public Barcode AddBarcode(Guid barcodeId, string code, BarcodeType type, decimal packQuantity = 1m)
    {
        EnsureNotArchived();

        var normalized = Barcode.Normalize(code);

        if (_barcodes.Any(b => string.Equals(b.Code, normalized, StringComparison.Ordinal)))
        {
            throw new DomainException($"Barcode {normalized} is already on this variant.");
        }

        var barcode = Barcode.Create(
            barcodeId,
            OrganizationId,
            Id,
            normalized,
            type,
            packQuantity,
            isPrimary: _barcodes.Count == 0);

        _barcodes.Add(barcode);
        return barcode;
    }

    public void MakeBarcodePrimary(Guid barcodeId)
    {
        var target = _barcodes.FirstOrDefault(b => b.Id == barcodeId)
            ?? throw new DomainException("That barcode does not belong to this variant.");

        foreach (var barcode in _barcodes)
        {
            barcode.ClearPrimary();
        }

        target.MakePrimary();
    }

    internal void MakeDefault()
    {
        if (Status == CatalogStatus.Archived)
        {
            throw new DomainException("An archived variant cannot be the default.");
        }

        IsDefault = true;
    }

    internal void ClearDefault() => IsDefault = false;

    public void Activate()
    {
        if (Status == CatalogStatus.Archived)
        {
            throw new DomainException("An archived variant cannot be reactivated.");
        }

        Status = CatalogStatus.Active;
    }

    public void Deactivate()
    {
        if (IsDefault)
        {
            throw new DomainException("Make another variant the default before withdrawing this one.");
        }

        Status = CatalogStatus.Inactive;
    }

    internal void Archive() => Status = CatalogStatus.Archived;

    public bool IsSellable => Status == CatalogStatus.Active;

    private void EnsureNotArchived()
    {
        if (Status == CatalogStatus.Archived)
        {
            throw new DomainException("An archived variant cannot be changed.");
        }
    }
}

/// <summary>What kind of barcode a code is, which decides how it is validated and read.</summary>
public enum BarcodeType
{
    /// <summary>13 digits with a check digit (the retail standard in Bangladesh).</summary>
    Ean13 = 0,

    /// <summary>8 digits with a check digit, used on small packs.</summary>
    Ean8 = 1,

    /// <summary>12 digits with a check digit (North American imports).</summary>
    UpcA = 2,

    /// <summary>Alphanumeric, no check digit; what most label printers produce.</summary>
    Code128 = 3,

    /// <summary>A code the shop invented for loose goods or in-house packs.</summary>
    Internal = 4,

    /// <summary>
    /// A scale label whose digits carry the weight or price. Only the item part identifies the
    /// variant; the embedded quantity is read by the POS (FR-CAT-03).
    /// </summary>
    WeightEmbedded = 5,
}

/// <summary>
/// A scannable code for a variant. Unique per tenant, never globally: two shops can and do print
/// their own internal codes, and a global unique constraint would make one of them fail for a
/// reason that has nothing to do with them (ADR-0004).
/// </summary>
public sealed class Barcode : TenantEntity
{
    private Barcode()
    {
    }

    private Barcode(
        Guid id,
        Guid organizationId,
        Guid productVariantId,
        string code,
        BarcodeType type,
        decimal packQuantity,
        bool isPrimary)
        : base(id, organizationId)
    {
        ProductVariantId = productVariantId;
        Code = code;
        Type = type;
        PackQuantity = packQuantity;
        IsPrimary = isPrimary;
    }

    public Guid ProductVariantId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public BarcodeType Type { get; private set; }

    /// <summary>
    /// How many of the variant one scan of this code sells. A case barcode with
    /// <c>PackQuantity = 24</c> rings up 24 pieces, which is the difference between a wholesaler
    /// being usable and not.
    /// </summary>
    public decimal PackQuantity { get; private set; }

    public bool IsPrimary { get; private set; }

    internal static Barcode Create(
        Guid id,
        Guid organizationId,
        Guid productVariantId,
        string code,
        BarcodeType type,
        decimal packQuantity,
        bool isPrimary)
    {
        var normalized = Normalize(code);

        if (!IsWellFormed(normalized, type))
        {
            throw new DomainException(
                $"'{normalized}' is not a valid {type} barcode. Check the digits, or record it as Internal.");
        }

        if (packQuantity <= 0m)
        {
            throw new DomainException("A barcode's pack quantity must be greater than zero.");
        }

        return new Barcode(
            id,
            organizationId,
            productVariantId,
            normalized,
            type,
            RoundingPolicy.RoundQuantity(packQuantity),
            isPrimary);
    }

    /// <summary>
    /// Trims and upper-cases a scanned code. Scanners sometimes append whitespace and some send
    /// lower-case letters for Code 128, and a lookup that misses because of that looks to a cashier
    /// like a missing product.
    /// </summary>
    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A barcode is required.", nameof(code));
        }

        var trimmed = code.Trim().ToUpperInvariant();

        if (trimmed.Length > 64)
        {
            throw new ArgumentException("A barcode must be at most 64 characters.", nameof(code));
        }

        return trimmed;
    }

    /// <summary>
    /// Checks the digits and the check digit for the fixed-length symbologies. The check digit is
    /// worth verifying on entry: a mistyped EAN-13 that is stored anyway will never be scanned, and
    /// the shop concludes the scanner is broken.
    /// </summary>
    public static bool IsWellFormed(string code, BarcodeType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return type switch
        {
            BarcodeType.Ean13 => HasCheckDigit(code, 13),
            BarcodeType.Ean8 => HasCheckDigit(code, 8),
            BarcodeType.UpcA => HasCheckDigit(code, 12),

            // A weight-embedded label's digits are a price or a weight that changes per item, so the
            // check digit is recomputed by the scale and not ours to verify.
            BarcodeType.WeightEmbedded => code.All(char.IsAsciiDigit) && code.Length is >= 8 and <= 20,
            BarcodeType.Code128 or BarcodeType.Internal => code.Length is >= 1 and <= 64,
            _ => false,
        };
    }

    /// <summary>The GS1 modulo-10 check digit for a numeric code of the given total length.</summary>
    public static int ComputeCheckDigit(string digitsWithoutCheck)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digitsWithoutCheck);

        var sum = 0;

        // Weights alternate 3,1,3,1… counting from the rightmost digit before the check digit.
        for (var i = 0; i < digitsWithoutCheck.Length; i++)
        {
            var digit = digitsWithoutCheck[^(i + 1)] - '0';
            sum += i % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - (sum % 10)) % 10;
    }

    private static bool HasCheckDigit(string code, int length)
    {
        if (code.Length != length || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        return ComputeCheckDigit(code[..^1]) == code[^1] - '0';
    }

    public void SetPackQuantity(decimal packQuantity)
    {
        if (packQuantity <= 0m)
        {
            throw new DomainException("A barcode's pack quantity must be greater than zero.");
        }

        PackQuantity = RoundingPolicy.RoundQuantity(packQuantity);
    }

    internal void MakePrimary() => IsPrimary = true;

    internal void ClearPrimary() => IsPrimary = false;
}

/// <summary>An image of a product, stored by reference. Binaries never go in the database.</summary>
public sealed class ProductImage : TenantEntity
{
    private ProductImage()
    {
    }

    private ProductImage(Guid id, Guid organizationId, Guid productId, string storageKey, bool isPrimary)
        : base(id, organizationId)
    {
        ProductId = productId;
        StorageKey = storageKey;
        IsPrimary = isPrimary;
    }

    public Guid ProductId { get; private set; }

    /// <summary>Path or object key in the configured file store, not the bytes.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public string? AltText { get; private set; }

    public bool IsPrimary { get; private set; }

    public int SortOrder { get; private set; }

    public static ProductImage Create(
        Guid id,
        Guid organizationId,
        Guid productId,
        string storageKey,
        bool isPrimary = false,
        string? altText = null,
        int sortOrder = 0) =>
        new(id, organizationId, productId, CatalogText.Required(storageKey, nameof(storageKey), 512), isPrimary)
        {
            AltText = CatalogText.Optional(altText, 200),
            SortOrder = sortOrder,
        };

    public void MakePrimary() => IsPrimary = true;

    public void ClearPrimary() => IsPrimary = false;
}

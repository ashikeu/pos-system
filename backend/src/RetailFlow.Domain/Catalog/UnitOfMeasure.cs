using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Catalog;

/// <summary>What a unit measures. Conversions are only ever allowed inside one family.</summary>
public enum UomKind
{
    /// <summary>Discrete items: piece, pack, case.</summary>
    Count = 0,

    /// <summary>Mass: gram, kilogram.</summary>
    Weight = 1,

    /// <summary>Volume: millilitre, litre.</summary>
    Volume = 2,

    /// <summary>Length: centimetre, metre.</summary>
    Length = 3,
}

/// <summary>
/// A unit a product is sold or stocked in (FR-CAT-04). <see cref="DecimalPlaces"/> decides whether
/// fractional quantities are legal: 1.5 kg of rice is fine, 1.5 bottles is not, and a POS that
/// allows the second one produces stock figures nobody can reconcile.
/// </summary>
public sealed class UnitOfMeasure : TenantEntity
{
    private UnitOfMeasure()
    {
    }

    private UnitOfMeasure(Guid id, Guid organizationId, string code, string name, UomKind kind, int decimalPlaces)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        Kind = kind;
        DecimalPlaces = decimalPlaces;
        Status = CatalogStatus.Active;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public UomKind Kind { get; private set; }

    /// <summary>
    /// Decimal places a quantity in this unit may carry, 0–3. Capped at 3 because that is the
    /// quantity scale the whole system stores (ADR-0006).
    /// </summary>
    public int DecimalPlaces { get; private set; }

    public CatalogStatus Status { get; private set; }

    public static UnitOfMeasure Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        UomKind kind = UomKind.Count,
        int decimalPlaces = 0)
    {
        if (decimalPlaces is < 0 or > RoundingPolicy.QuantityScale)
        {
            throw new DomainException(
                $"A unit may carry between 0 and {RoundingPolicy.QuantityScale} decimal places.");
        }

        return new UnitOfMeasure(
            id,
            organizationId,
            CatalogText.Code(code, nameof(code), 16),
            CatalogText.Required(name, nameof(name), 100),
            kind,
            decimalPlaces);
    }

    public void Rename(string name, string? nameBn)
    {
        Name = CatalogText.Required(name, nameof(name), 100);
        NameBn = CatalogText.Optional(nameBn, 100);
    }

    /// <summary>True when a quantity is expressible in this unit.</summary>
    public bool AllowsQuantity(decimal quantity) =>
        decimal.Round(quantity, DecimalPlaces, MidpointRounding.AwayFromZero) == quantity;

    /// <summary>Rounds a quantity to what this unit can express.</summary>
    public decimal Normalize(decimal quantity) =>
        decimal.Round(quantity, DecimalPlaces, MidpointRounding.AwayFromZero);

    public void Activate() => Status = CatalogStatus.Active;

    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Archive() => Status = CatalogStatus.Archived;
}

/// <summary>
/// How many <c>To</c> units one <c>From</c> unit contains — a case of 24 pieces is
/// <c>From=CASE, To=PCS, Factor=24</c>. Both directions are derivable from one row, so only one is
/// stored: two rows that disagree would be a silent stock error.
/// </summary>
public sealed class UomConversion : TenantEntity
{
    private UomConversion()
    {
    }

    private UomConversion(Guid id, Guid organizationId, Guid fromUomId, Guid toUomId, decimal factor)
        : base(id, organizationId)
    {
        FromUomId = fromUomId;
        ToUomId = toUomId;
        Factor = factor;
    }

    public Guid FromUomId { get; private set; }

    public Guid ToUomId { get; private set; }

    /// <summary>Units of <see cref="ToUomId"/> in one <see cref="FromUomId"/>. Always positive.</summary>
    public decimal Factor { get; private set; }

    public static UomConversion Create(
        Guid id,
        Guid organizationId,
        UnitOfMeasure from,
        UnitOfMeasure to,
        decimal factor)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Id == to.Id)
        {
            throw new DomainException("A unit does not need a conversion to itself.");
        }

        if (from.Kind != to.Kind)
        {
            throw new DomainException(
                $"A {from.Kind} unit cannot be converted to a {to.Kind} unit. Add a pack size on the variant instead.");
        }

        if (factor <= 0m)
        {
            throw new DomainException("A conversion factor must be greater than zero.");
        }

        return new UomConversion(id, organizationId, from.Id, to.Id, RoundingPolicy.Round6(factor));
    }

    public void ChangeFactor(decimal factor)
    {
        if (factor <= 0m)
        {
            throw new DomainException("A conversion factor must be greater than zero.");
        }

        Factor = RoundingPolicy.Round6(factor);
    }

    /// <summary>Converts a quantity in <see cref="FromUomId"/> into <see cref="ToUomId"/>.</summary>
    public decimal ToBase(decimal quantity) => RoundingPolicy.RoundQuantity(quantity * Factor);

    /// <summary>Converts a quantity in <see cref="ToUomId"/> back into <see cref="FromUomId"/>.</summary>
    public decimal FromBase(decimal quantity) => RoundingPolicy.RoundQuantity(quantity / Factor);
}

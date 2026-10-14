using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Pricing;

/// <summary>How a price relates to the tax on it.</summary>
public enum TaxTreatment
{
    /// <summary>The price excludes tax; tax is added on top.</summary>
    Exclusive = 0,

    /// <summary>The displayed price already contains tax; tax is extracted from it.</summary>
    Inclusive = 1,

    /// <summary>Outside the scope of VAT. Tax is zero and the reason is recorded.</summary>
    Exempt = 2,

    /// <summary>In scope but rated at zero (exports, specified essentials).</summary>
    ZeroRated = 3,
}

/// <summary>
/// A tax treatment a group of products shares — "standard 15%", "zero-rated essentials",
/// "exempt services". Rates are not here: they live in versioned, effective-dated rules, because a
/// rate change must not rewrite what yesterday's sales were taxed at (ADR-0019).
/// </summary>
public sealed class TaxClass : TenantEntity
{
    private TaxClass()
    {
    }

    private TaxClass(Guid id, Guid organizationId, string code, string name, TaxTreatment treatment)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        Treatment = treatment;
        IsActive = true;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The treatment products in this class get. <see cref="TaxTreatment.Exclusive"/> and
    /// <see cref="TaxTreatment.Inclusive"/> are overridden per rule version, because a shop can
    /// switch to displaying tax-inclusive prices without reclassifying its catalog.
    /// </summary>
    public TaxTreatment Treatment { get; private set; }

    /// <summary>Why this class is exempt or zero-rated — the SRO or section, for the VAT return.</summary>
    public string? ExemptionReason { get; private set; }

    /// <summary>The class applied to a product that names none. Exactly one per tenant.</summary>
    public bool IsDefault { get; private set; }

    public bool IsActive { get; private set; }

    public static TaxClass Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        TaxTreatment treatment = TaxTreatment.Exclusive,
        bool isDefault = false)
    {
        var taxClass = new TaxClass(
            id,
            organizationId,
            PricingText.Code(code, nameof(code)),
            PricingText.Required(name, nameof(name), 100),
            treatment);

        if (isDefault)
        {
            taxClass.MakeDefault();
        }

        return taxClass;
    }

    public void Rename(string name) => Name = PricingText.Required(name, nameof(name), 100);

    public void SetExemption(TaxTreatment treatment, string? reason)
    {
        if (treatment is TaxTreatment.Exempt or TaxTreatment.ZeroRated && string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(
                "An exempt or zero-rated class needs a reason: the VAT return has to distinguish them.");
        }

        Treatment = treatment;
        ExemptionReason = PricingText.Optional(reason, 256);
    }

    public void MakeDefault() => IsDefault = true;

    public void ClearDefault() => IsDefault = false;

    public void Deactivate()
    {
        if (IsDefault)
        {
            throw new DomainException("Make another tax class the default before deactivating this one.");
        }

        IsActive = false;
    }

    public void Activate() => IsActive = true;
}

/// <summary>
/// Which rates apply to a tax class, optionally only at one branch. The rule itself is just the
/// scope; the numbers are in its versions, so a rate change is an append, never an update
/// (ADR-0019).
/// </summary>
public sealed class TaxRule : TenantEntity
{
    private readonly List<TaxRuleVersion> _versions = [];

    private TaxRule()
    {
    }

    private TaxRule(Guid id, Guid organizationId, Guid taxClassId, Guid? branchId, string name)
        : base(id, organizationId)
    {
        TaxClassId = taxClassId;
        BranchId = branchId;
        Name = name;
    }

    public Guid TaxClassId { get; private set; }

    /// <summary>Null for the organization-wide rule; set for a branch that differs.</summary>
    public Guid? BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public IReadOnlyCollection<TaxRuleVersion> Versions => _versions;

    public static TaxRule Create(Guid id, Guid organizationId, Guid taxClassId, string name, Guid? branchId = null)
    {
        if (taxClassId == Guid.Empty)
        {
            throw new ArgumentException("A tax rule needs a tax class.", nameof(taxClassId));
        }

        return new TaxRule(id, organizationId, taxClassId, branchId, PricingText.Required(name, nameof(name), 100));
    }

    public void Rename(string name) => Name = PricingText.Required(name, nameof(name), 100);

    /// <summary>
    /// Opens a new version from <paramref name="effectiveFromUtc"/> and closes the previous open one
    /// the instant before. Versions therefore tile the timeline without gaps or overlaps, which is
    /// what makes "what rate applied to this sale" answerable years later.
    /// </summary>
    public TaxRuleVersion AddVersion(
        Guid versionId,
        decimal vatRate,
        decimal sdRate,
        bool sdInVatBase,
        bool priceIncludesTax,
        DateTimeOffset effectiveFromUtc,
        string? sroReference = null,
        string? notes = null)
    {
        // Compared against the newest version, not the one effective on the new date: a date before
        // every existing version is exactly the backdating this refuses, and asking "what was
        // effective then" would answer null and let it through (found by test).
        var latest = _versions.OrderByDescending(v => v.EffectiveFromUtc).FirstOrDefault();

        if (latest is not null)
        {
            if (effectiveFromUtc <= latest.EffectiveFromUtc)
            {
                throw new DomainException(
                    "A new tax version must start after the one it replaces. Backdating a rate would change "
                        + "what past sales were taxed at.");
            }

            if (latest.IsCurrent)
            {
                latest.CloseAt(effectiveFromUtc);
            }
        }

        var version = TaxRuleVersion.Create(
            versionId,
            OrganizationId,
            Id,
            _versions.Count == 0 ? 1 : _versions.Max(v => v.Version) + 1,
            vatRate,
            sdRate,
            sdInVatBase,
            priceIncludesTax,
            effectiveFromUtc,
            sroReference,
            notes);

        _versions.Add(version);
        return version;
    }

    /// <summary>The version in force at an instant, or null when the rule had not started yet.</summary>
    public TaxRuleVersion? CurrentVersion(DateTimeOffset atUtc) =>
        _versions
            .Where(v => v.IsEffectiveAt(atUtc))
            .OrderByDescending(v => v.EffectiveFromUtc)
            .FirstOrDefault();
}

/// <summary>
/// The rates in force over a period. Immutable except for being closed when its successor opens:
/// a VAT return filed last quarter must still be reproducible from these rows.
/// </summary>
public sealed class TaxRuleVersion : TenantEntity
{
    private TaxRuleVersion()
    {
    }

    private TaxRuleVersion(
        Guid id,
        Guid organizationId,
        Guid taxRuleId,
        int version,
        decimal vatRate,
        decimal sdRate,
        bool sdInVatBase,
        bool priceIncludesTax,
        DateTimeOffset effectiveFromUtc)
        : base(id, organizationId)
    {
        TaxRuleId = taxRuleId;
        Version = version;
        VatRate = vatRate;
        SdRate = sdRate;
        SdInVatBase = sdInVatBase;
        PriceIncludesTax = priceIncludesTax;
        EffectiveFromUtc = effectiveFromUtc;
    }

    public Guid TaxRuleId { get; private set; }

    public int Version { get; private set; }

    /// <summary>VAT rate as a fraction: 0.15 for 15%.</summary>
    public decimal VatRate { get; private set; }

    /// <summary>Supplementary duty rate as a fraction. Zero for most goods.</summary>
    public decimal SdRate { get; private set; }

    /// <summary>
    /// Whether SD forms part of the VAT base — the common Bangladeshi treatment, and the assumption
    /// an accountant has to confirm before a production pilot (A-02).
    /// </summary>
    public bool SdInVatBase { get; private set; }

    /// <summary>Whether prices under this version are displayed tax-inclusive.</summary>
    public bool PriceIncludesTax { get; private set; }

    public DateTimeOffset EffectiveFromUtc { get; private set; }

    /// <summary>Exclusive end of the period. Null while this is the current version.</summary>
    public DateTimeOffset? EffectiveToUtc { get; private set; }

    /// <summary>The SRO or circular this rate comes from, for the audit file.</summary>
    public string? SroReference { get; private set; }

    public string? Notes { get; private set; }

    internal static TaxRuleVersion Create(
        Guid id,
        Guid organizationId,
        Guid taxRuleId,
        int version,
        decimal vatRate,
        decimal sdRate,
        bool sdInVatBase,
        bool priceIncludesTax,
        DateTimeOffset effectiveFromUtc,
        string? sroReference,
        string? notes)
    {
        ValidateRate(vatRate, nameof(vatRate));
        ValidateRate(sdRate, nameof(sdRate));

        return new TaxRuleVersion(
            id,
            organizationId,
            taxRuleId,
            version,
            RoundingPolicy.Round6(vatRate),
            RoundingPolicy.Round6(sdRate),
            sdInVatBase,
            priceIncludesTax,
            effectiveFromUtc)
        {
            SroReference = PricingText.Optional(sroReference, 64),
            Notes = PricingText.Optional(notes, 500),
        };
    }

    internal void CloseAt(DateTimeOffset atUtc)
    {
        if (atUtc <= EffectiveFromUtc)
        {
            throw new DomainException("A tax version cannot end before it starts.");
        }

        EffectiveToUtc = atUtc;
    }

    public bool IsEffectiveAt(DateTimeOffset atUtc) =>
        EffectiveFromUtc <= atUtc && (EffectiveToUtc is null || atUtc < EffectiveToUtc);

    public bool IsCurrent => EffectiveToUtc is null;

    private static void ValidateRate(decimal rate, string name)
    {
        // An upper bound of 1 (100%) is deliberate: a rate entered as 15 instead of 0.15 is the
        // single most likely data-entry error here, and it would multiply every bill by sixteen.
        if (rate is < 0m or > 1m)
        {
            throw new DomainException(
                $"{name} must be a fraction between 0 and 1 — enter 15% as 0.15, not 15.");
        }
    }
}

/// <summary>Normalisation shared by the pricing entities.</summary>
internal static class PricingText
{
    public static string Code(string value, string parameterName, int maxLength = 32)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var trimmed = value.Trim().ToUpperInvariant();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return trimmed;
    }

    public static string Required(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    public static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

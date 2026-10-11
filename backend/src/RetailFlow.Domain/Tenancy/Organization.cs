using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>Lifecycle state of a tenant (FR-TEN-06).</summary>
public enum OrganizationStatus
{
    /// <summary>Trading normally.</summary>
    Active = 0,

    /// <summary>Blocked for non-payment or administrative reasons: read-only access only.</summary>
    Suspended = 1,

    /// <summary>Permanently closed; retained for the statutory period before purge (NFR-09).</summary>
    Closed = 2,
}

/// <summary>
/// A tenant. Its <see cref="Entity.Id"/> is the organization id carried by every tenant-owned
/// record, so this aggregate is the root of the isolation model (ADR-0004). Phase 4 adds settings,
/// subscriptions and provisioning; this is the minimum the platform needs to scope data.
/// </summary>
public sealed class Organization : AuditableAggregateRoot
{
    private Organization()
    {
    }

    private Organization(
        Guid id,
        string code,
        string name,
        string currencyCode,
        string timeZoneId,
        string defaultLanguage)
        : base(id)
    {
        Code = code;
        Name = name;
        CurrencyCode = currencyCode;
        TimeZoneId = timeZoneId;
        DefaultLanguage = defaultLanguage;
        Status = OrganizationStatus.Active;
    }

    /// <summary>Short unique code used in document numbers and support conversations.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Registered legal name, printed on VAT documents when present.</summary>
    public string? LegalName { get; private set; }

    /// <summary>Business Identification Number issued by the NBR, printed on VAT invoices.</summary>
    public string? BinNumber { get; private set; }

    /// <summary>ISO 4217 code; BDT by default (FR-LOC-01, assumption A-01: one currency per tenant).</summary>
    public string CurrencyCode { get; private set; } = Money.DefaultCurrencyCode;

    /// <summary>IANA time zone used when a branch does not define its own (ADR-0007).</summary>
    public string TimeZoneId { get; private set; } = BusinessDay.DefaultTimeZoneId;

    /// <summary>Default UI language, <c>en</c> or <c>bn</c> (FR-LOC-02).</summary>
    public string DefaultLanguage { get; private set; } = "en";

    public OrganizationStatus Status { get; private set; }

    public DateTimeOffset? SuspendedAtUtc { get; private set; }

    public string? SuspendedReason { get; private set; }

    public static Organization Create(
        Guid id,
        string code,
        string name,
        string? currencyCode = null,
        string? timeZoneId = null,
        string? defaultLanguage = null)
    {
        var normalizedCode = Normalize(code, nameof(code), 32).ToUpperInvariant();
        var normalizedName = Normalize(name, nameof(name), 200);
        var normalizedCurrency = (currencyCode ?? Money.DefaultCurrencyCode).Trim().ToUpperInvariant();

        if (normalizedCurrency.Length != 3)
        {
            throw new ArgumentException("Currency code must be a 3-letter ISO 4217 code.", nameof(currencyCode));
        }

        var language = (defaultLanguage ?? "en").Trim().ToLowerInvariant();
        if (language is not ("en" or "bn"))
        {
            throw new ArgumentException("Supported languages are 'en' and 'bn'.", nameof(defaultLanguage));
        }

        return new Organization(
            id,
            normalizedCode,
            normalizedName,
            normalizedCurrency,
            string.IsNullOrWhiteSpace(timeZoneId) ? BusinessDay.DefaultTimeZoneId : timeZoneId.Trim(),
            language);
    }

    public void Rename(string name) => Name = Normalize(name, nameof(name), 200);

    public void SetLegalIdentity(string? legalName, string? binNumber)
    {
        LegalName = string.IsNullOrWhiteSpace(legalName) ? null : legalName.Trim();
        BinNumber = string.IsNullOrWhiteSpace(binNumber) ? null : binNumber.Trim();
    }

    public void Suspend(DateTimeOffset atUtc, string reason)
    {
        if (Status == OrganizationStatus.Closed)
        {
            throw new DomainException("A closed organization cannot be suspended.");
        }

        Status = OrganizationStatus.Suspended;
        SuspendedAtUtc = atUtc;
        SuspendedReason = Normalize(reason, nameof(reason), 500);
    }

    public void Reactivate()
    {
        if (Status == OrganizationStatus.Closed)
        {
            throw new DomainException("A closed organization cannot be reactivated.");
        }

        Status = OrganizationStatus.Active;
        SuspendedAtUtc = null;
        SuspendedReason = null;
    }

    public void Close(DateTimeOffset atUtc, string reason)
    {
        Status = OrganizationStatus.Closed;
        SuspendedAtUtc = atUtc;
        SuspendedReason = Normalize(reason, nameof(reason), 500);
    }

    private static string Normalize(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

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
/// record, so this aggregate is the root of the isolation model (ADR-0004). Its configuration lives
/// in <see cref="OrganizationSettings"/> and its plan in <see cref="OrganizationSubscription"/>,
/// both created by provisioning.
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

    public string? NameBn { get; private set; }

    /// <summary>Head-office address, printed on VAT documents.</summary>
    public string? AddressLine { get; private set; }

    public string? City { get; private set; }

    public string? PhoneNumber { get; private set; }

    /// <summary>Billing and notification address for the account owner.</summary>
    public string? ContactEmail { get; private set; }

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

    /// <summary>Updates the details that appear on documents and in the admin screens.</summary>
    public void UpdateProfile(
        string name,
        string? nameBn,
        string? addressLine,
        string? city,
        string? phoneNumber,
        string? contactEmail)
    {
        Name = Normalize(name, nameof(name), 200);
        NameBn = Optional(nameBn, 200);
        AddressLine = Optional(addressLine, 500);
        City = Optional(city, 100);
        PhoneNumber = Optional(phoneNumber, 32);
        ContactEmail = Optional(contactEmail, 256)?.ToLowerInvariant();
    }

    /// <summary>
    /// Changes the tenant's time zone and default language. The currency is deliberately not
    /// changeable: every stored amount is in it, and re-denominating historical money is not a
    /// settings change (assumption A-01).
    /// </summary>
    public void SetLocalization(string timeZoneId, string defaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("Time zone is required.", nameof(timeZoneId));
        }

        var language = (defaultLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (language is not ("en" or "bn"))
        {
            throw new ArgumentException("Supported languages are 'en' and 'bn'.", nameof(defaultLanguage));
        }

        TimeZoneId = timeZoneId.Trim();
        DefaultLanguage = language;
    }

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

        Raise(new OrganizationSuspended(Guid.Empty, atUtc, Id, Code, SuspendedReason));
    }

    public void Reactivate(DateTimeOffset atUtc)
    {
        if (Status == OrganizationStatus.Closed)
        {
            throw new DomainException("A closed organization cannot be reactivated.");
        }

        Status = OrganizationStatus.Active;
        SuspendedAtUtc = null;
        SuspendedReason = null;

        Raise(new OrganizationReactivated(Guid.Empty, atUtc, Id, Code));
    }

    public void Close(DateTimeOffset atUtc, string reason)
    {
        Status = OrganizationStatus.Closed;
        SuspendedAtUtc = atUtc;
        SuspendedReason = Normalize(reason, nameof(reason), 500);

        Raise(new OrganizationClosed(Guid.Empty, atUtc, Id, Code, SuspendedReason));
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
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

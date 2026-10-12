using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>
/// A trading location. Branches own warehouses and POS terminals, scope user access, and define
/// the time zone and business-day start used to date their transactions (FR-TEN-01, ADR-0007).
/// Overrides for that location live in <see cref="BranchSettings"/>.
/// </summary>
public sealed class Branch : TenantEntity
{
    private Branch()
    {
    }

    private Branch(Guid id, Guid organizationId, string code, string name, string timeZoneId, TimeOnly businessDayStart)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        TimeZoneId = timeZoneId;
        BusinessDayStart = businessDayStart;
        IsActive = true;
    }

    /// <summary>Short code, unique within the organization; appears in document numbers.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string? AddressLine { get; private set; }

    public string? City { get; private set; }

    public string? PhoneNumber { get; private set; }

    /// <summary>IANA time zone of this location (ADR-0007).</summary>
    public string TimeZoneId { get; private set; } = BusinessDay.DefaultTimeZoneId;

    /// <summary>
    /// Local time at which the trading day rolls over. Midnight for most shops; a late-night
    /// pharmacy may start at 06:00 so after-midnight sales stay on the previous trading day.
    /// </summary>
    public TimeOnly BusinessDayStart { get; private set; }

    public bool IsActive { get; private set; }

    public static Branch Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        string? timeZoneId = null,
        TimeOnly? businessDayStart = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Branch code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Branch name is required.", nameof(name));
        }

        return new Branch(
            id,
            organizationId,
            code.Trim().ToUpperInvariant(),
            name.Trim(),
            string.IsNullOrWhiteSpace(timeZoneId) ? BusinessDay.DefaultTimeZoneId : timeZoneId.Trim(),
            businessDayStart ?? TimeOnly.MinValue);
    }

    public void UpdateDetails(string name, string? nameBn, string? addressLine, string? city, string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Branch name is required.", nameof(name));
        }

        Name = name.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        AddressLine = string.IsNullOrWhiteSpace(addressLine) ? null : addressLine.Trim();
        City = string.IsNullOrWhiteSpace(city) ? null : city.Trim();
        PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
    }

    public void SetBusinessCalendar(string timeZoneId, TimeOnly businessDayStart)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("Time zone is required.", nameof(timeZoneId));
        }

        TimeZoneId = timeZoneId.Trim();
        BusinessDayStart = businessDayStart;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    /// <summary>Resolves the business date of an instant for this branch (ADR-0007).</summary>
    public DateOnly BusinessDateFor(DateTimeOffset instantUtc) =>
        BusinessDay.DateFor(instantUtc, BusinessDay.ResolveTimeZone(TimeZoneId), BusinessDayStart);
}

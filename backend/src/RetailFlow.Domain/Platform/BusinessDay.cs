namespace RetailFlow.Domain.Platform;

/// <summary>
/// Converts between the UTC instants the system stores and the branch-local business date it
/// reports on (ADR-0007). A shop that trades past midnight must still see those sales on the
/// trading day they belong to, so the business day can start at a configured local time.
/// </summary>
public static class BusinessDay
{
    /// <summary>The platform default: Bangladesh Standard Time, UTC+6, no daylight saving.</summary>
    public const string DefaultTimeZoneId = "Asia/Dhaka";

    /// <summary>
    /// Returns the business date an instant falls on for a branch.
    /// </summary>
    /// <param name="instantUtc">The instant, in UTC.</param>
    /// <param name="timeZone">The branch time zone.</param>
    /// <param name="dayStart">
    /// Local time at which the business day begins. Midnight means the business date equals the
    /// local calendar date; 06:00 means sales rung at 02:00 belong to the previous day.
    /// </param>
    public static DateOnly DateFor(DateTimeOffset instantUtc, TimeZoneInfo timeZone, TimeOnly dayStart)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = TimeZoneInfo.ConvertTime(instantUtc, timeZone);
        var localDateTime = local.DateTime;
        var date = DateOnly.FromDateTime(localDateTime);

        return TimeOnly.FromDateTime(localDateTime) < dayStart ? date.AddDays(-1) : date;
    }

    /// <summary>
    /// Returns the UTC half-open interval <c>[start, end)</c> covering a business date, for use in
    /// report queries. The end bound is exclusive so no transaction is counted twice.
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) RangeUtc(
        DateOnly businessDate,
        TimeZoneInfo timeZone,
        TimeOnly dayStart)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var start = ToUtc(businessDate.ToDateTime(dayStart), timeZone);
        var end = ToUtc(businessDate.AddDays(1).ToDateTime(dayStart), timeZone);

        return (start, end);
    }

    /// <summary>
    /// Resolves a time zone by IANA id, falling back to the Windows id where the host has no IANA
    /// data, and finally to a fixed UTC+6 zone so a terminal never fails to ring a sale because of
    /// a missing time-zone database.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var id = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall through to the mapping attempt below.
        }
        catch (InvalidTimeZoneException)
        {
            // Corrupt zone data: treat as not found.
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId)
            && TryFind(windowsId, out var byWindowsId))
        {
            return byWindowsId;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId)
            && TryFind(ianaId, out var byIanaId))
        {
            return byIanaId;
        }

        return TimeZoneInfo.CreateCustomTimeZone("RetailFlow/UTC+06", TimeSpan.FromHours(6), "UTC+06", "UTC+06");
    }

    private static bool TryFind(string? id, out TimeZoneInfo timeZone)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
                // Ignored: the caller falls back to a fixed offset.
            }
            catch (InvalidTimeZoneException)
            {
                // Ignored: the caller falls back to a fixed offset.
            }
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    private static DateTimeOffset ToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var offset = timeZone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}

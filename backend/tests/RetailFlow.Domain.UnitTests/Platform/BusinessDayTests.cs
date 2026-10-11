using FluentAssertions;
using RetailFlow.Domain.Platform;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Platform;

public class BusinessDayTests
{
    private static readonly TimeZoneInfo Dhaka = BusinessDay.ResolveTimeZone(BusinessDay.DefaultTimeZoneId);

    [Fact]
    public void ResolveTimeZone_finds_Dhaka_at_UTC_plus_six() =>
        Dhaka.GetUtcOffset(new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be(TimeSpan.FromHours(6));

    [Fact]
    public void ResolveTimeZone_falls_back_to_a_fixed_offset_for_an_unknown_zone() =>
        BusinessDay.ResolveTimeZone("Mars/Olympus")
            .GetUtcOffset(DateTimeOffset.UtcNow).Should().Be(TimeSpan.FromHours(6));

    [Fact]
    public void A_sale_at_2030_UTC_belongs_to_the_next_local_day_in_Dhaka()
    {
        // 20:30 UTC is 02:30 the next day in Dhaka.
        var instant = new DateTimeOffset(2026, 10, 11, 20, 30, 0, TimeSpan.Zero);

        BusinessDay.DateFor(instant, Dhaka, TimeOnly.MinValue)
            .Should().Be(new DateOnly(2026, 10, 12));
    }

    [Fact]
    public void With_a_six_am_day_start_an_early_morning_sale_belongs_to_the_previous_trading_day()
    {
        // 02:30 local on the 12th, business day starts at 06:00 → still the 11th trading day.
        var instant = new DateTimeOffset(2026, 10, 11, 20, 30, 0, TimeSpan.Zero);

        BusinessDay.DateFor(instant, Dhaka, new TimeOnly(6, 0))
            .Should().Be(new DateOnly(2026, 10, 11));
    }

    [Fact]
    public void RangeUtc_covers_exactly_one_day_starting_at_the_configured_local_time()
    {
        var (startUtc, endUtc) = BusinessDay.RangeUtc(new DateOnly(2026, 10, 11), Dhaka, new TimeOnly(6, 0));

        startUtc.Should().Be(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero));   // 06:00 +06
        endUtc.Should().Be(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        (endUtc - startUtc).Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void RangeUtc_and_DateFor_agree_on_the_boundaries()
    {
        var date = new DateOnly(2026, 10, 11);
        var dayStart = new TimeOnly(6, 0);
        var (startUtc, endUtc) = BusinessDay.RangeUtc(date, Dhaka, dayStart);

        BusinessDay.DateFor(startUtc, Dhaka, dayStart).Should().Be(date);
        BusinessDay.DateFor(endUtc.AddTicks(-1), Dhaka, dayStart).Should().Be(date);
        BusinessDay.DateFor(endUtc, Dhaka, dayStart).Should().Be(date.AddDays(1));
        BusinessDay.DateFor(startUtc.AddTicks(-1), Dhaka, dayStart).Should().Be(date.AddDays(-1));
    }
}

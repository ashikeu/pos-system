using FluentAssertions;
using RetailFlow.Domain.Platform;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Platform;

public class UuidV7Tests
{
    [Fact]
    public void NewGuid_sets_the_version_and_variant_bits()
    {
        var id = UuidV7.NewGuid();

        UuidV7.IsUuidV7(id).Should().BeTrue();

        // Canonical text form: xxxxxxxx-xxxx-7xxx-[89ab]xxx-xxxxxxxxxxxx
        var text = id.ToString();
        text[14].Should().Be('7', "the version nibble must say 7");
        "89ab".Should().Contain(text[19].ToString(), "the variant nibble must be 8, 9, a or b");
    }

    [Fact]
    public void NewGuid_embeds_the_supplied_timestamp()
    {
        var timestamp = new DateTimeOffset(2026, 10, 11, 14, 30, 15, 250, TimeSpan.Zero);

        var id = UuidV7.NewGuid(timestamp);

        UuidV7.TryGetTimestamp(id).Should().Be(timestamp);
    }

    [Fact]
    public void Identifiers_sort_in_creation_order_even_within_one_millisecond()
    {
        var timestamp = new DateTimeOffset(2026, 10, 11, 14, 30, 15, 250, TimeSpan.Zero);

        var ids = Enumerable.Range(0, 200).Select(_ => UuidV7.NewGuid(timestamp)).ToList();

        var sorted = ids.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToList();
        sorted.Should().BeEquivalentTo(ids, options => options.WithStrictOrdering());
    }

    [Fact]
    public void An_earlier_instant_is_still_embedded_faithfully()
    {
        // An offline terminal whose clock is behind the server must keep its own business instant;
        // the generator must not clamp it to the highest timestamp already issued.
        var later = new DateTimeOffset(2026, 10, 11, 14, 30, 15, 500, TimeSpan.Zero);
        var earlier = later.AddMilliseconds(-50);

        UuidV7.NewGuid(later);
        var second = UuidV7.NewGuid(earlier);

        UuidV7.TryGetTimestamp(second).Should().Be(earlier);
    }

    [Fact]
    public void Repeated_generation_at_the_same_instant_stays_unique()
    {
        var instant = new DateTimeOffset(2026, 10, 11, 14, 30, 15, 500, TimeSpan.Zero);

        var ids = Enumerable.Range(0, 5_000).Select(_ => UuidV7.NewGuid(instant)).ToHashSet();

        ids.Should().HaveCount(5_000);
    }

    [Fact]
    public void Identifiers_are_unique_across_many_generations()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => UuidV7.NewGuid()).ToHashSet();

        ids.Should().HaveCount(20_000);
    }

    [Fact]
    public void TryGetTimestamp_returns_null_for_a_random_version_4_guid() =>
        UuidV7.TryGetTimestamp(Guid.NewGuid()).Should().BeNull();

    [Fact]
    public void NewGuid_rejects_instants_before_the_unix_epoch()
    {
        var act = () => UuidV7.NewGuid(new DateTimeOffset(1969, 1, 1, 0, 0, 0, TimeSpan.Zero));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

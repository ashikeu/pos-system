using FluentAssertions;
using RetailFlow.Infrastructure.Platform.Persistence.Records;
using Xunit;

namespace RetailFlow.IntegrationTests.Platform;

/// <summary>
/// Backoff arithmetic, checked without a database. Retry pacing decides whether a provider outage
/// recovers quietly or is hammered by every queued message at once (ADR-0011).
/// </summary>
public sealed class OutboxBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(15);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 80)]
    public void Backoff_doubles_with_each_attempt(int attempt, double expectedSeconds) =>
        OutboxMessage.ComputeBackoff(attempt, Base, Max, jitterFactor: 0)
            .TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);

    [Fact]
    public void Backoff_is_capped() =>
        OutboxMessage.ComputeBackoff(20, Base, Max, jitterFactor: 0).Should().Be(Max);

    [Fact]
    public void A_very_high_attempt_count_does_not_overflow() =>
        OutboxMessage.ComputeBackoff(int.MaxValue, Base, Max, jitterFactor: 0).Should().Be(Max);

    [Fact]
    public void Jitter_keeps_the_delay_within_the_configured_spread()
    {
        var delays = Enumerable.Range(0, 200)
            .Select(_ => OutboxMessage.ComputeBackoff(3, Base, Max, jitterFactor: 0.2).TotalSeconds)
            .ToArray();

        // Attempt 3 is nominally 20s, so with ±20% every value must fall in [16, 24].
        delays.Should().OnlyContain(d => d >= 16d && d <= 24d);
        delays.Distinct().Should().HaveCountGreaterThan(1, "jitter must actually vary the delay");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_attempt_number_is_rejected(int attempt)
    {
        var act = () => OutboxMessage.ComputeBackoff(attempt, Base, Max, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void An_invalid_jitter_factor_is_rejected(double jitter)
    {
        var act = () => OutboxMessage.ComputeBackoff(1, Base, Max, jitter);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

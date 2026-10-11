namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>Retention policy for idempotency records (ADR-0010, NFR-09).</summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// How long a key and its stored response stay replayable. Long enough to cover an extended
    /// offline period plus a safety margin; a purge job removes anything older.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(90);
}

/// <summary>Limits that apply while a terminal is offline (FR-OFF-09, ADR-0017).</summary>
public sealed class OfflineLimitOptions
{
    public TimeSpan MaxOfflineDuration { get; init; } = TimeSpan.FromHours(72);

    public decimal MaxOfflineSaleAmount { get; init; } = 50_000m;

    public decimal MaxCumulativeOfflineAmount { get; init; } = 200_000m;

    /// <summary>
    /// Tolerated difference between a device clock and the server before a synced record is flagged
    /// for review (ADR-0007).
    /// </summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(15);
}

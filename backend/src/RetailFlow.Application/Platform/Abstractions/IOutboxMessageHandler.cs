namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>
/// Handles a domain event delivered from the outbox. Implementations must be idempotent: the
/// dispatcher guarantees at-least-once delivery, so a handler can see the same message twice after
/// a crash between "handled" and "marked processed" (ADR-0011).
/// </summary>
public interface IOutboxMessageHandler
{
    /// <summary>The event type this handler reacts to, e.g. <c>sales.sale-completed</c>.</summary>
    string MessageType { get; }

    /// <summary>
    /// Processes the message. Throwing schedules a retry with backoff; returning normally marks the
    /// message delivered.
    /// </summary>
    Task HandleAsync(string payloadJson, CancellationToken cancellationToken = default);
}

/// <summary>Dispatcher settings, bound from the <c>Outbox</c> configuration section.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Messages claimed per poll.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>Delay between polls when the queue was empty.</summary>
    public int PollIntervalSeconds { get; init; } = 5;

    /// <summary>
    /// How long a claim is held. A dispatcher that dies mid-message leaves its lease to expire,
    /// after which another instance may take the message over.
    /// </summary>
    public int LeaseSeconds { get; init; } = 60;

    /// <summary>Attempts before a message is dead-lettered for human attention.</summary>
    public int MaxAttempts { get; init; } = 10;

    public int BaseBackoffSeconds { get; init; } = 5;

    public int MaxBackoffSeconds { get; init; } = 900;

    /// <summary>
    /// Random spread applied to each backoff, as a fraction. Without it, every message queued
    /// during an outage retries in lockstep and hammers the dependency that just recovered.
    /// </summary>
    public double JitterFactor { get; init; } = 0.2;
}

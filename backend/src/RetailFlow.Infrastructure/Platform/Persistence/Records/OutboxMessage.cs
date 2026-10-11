namespace RetailFlow.Infrastructure.Platform.Persistence.Records;

/// <summary>Delivery state of an outbox message (ADR-0011).</summary>
public enum OutboxMessageStatus
{
    /// <summary>Waiting to be dispatched.</summary>
    Pending = 0,

    /// <summary>Leased by a dispatcher instance.</summary>
    Processing = 1,

    /// <summary>Delivered to all handlers.</summary>
    Processed = 2,

    /// <summary>Delivery failed and a retry is scheduled.</summary>
    Failed = 3,

    /// <summary>Retries exhausted; requires human attention and raises an alert.</summary>
    DeadLettered = 4,
}

/// <summary>
/// A domain event written in the same transaction as the state change that produced it, then
/// delivered asynchronously (ADR-0011). Publication can therefore never be lost, and never happen
/// for a change that rolled back.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Owning tenant, or <c>null</c> for platform-level events.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Wire name of the event, e.g. <c>sales.sale-completed</c>.</summary>
    public string MessageType { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = string.Empty;

    public OutboxMessageStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When this message becomes eligible for dispatch.</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    /// <summary>Identifies the dispatcher instance holding the lease.</summary>
    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public string? CorrelationId { get; private set; }

    public byte[]? ConcurrencyToken { get; private set; }

    public static OutboxMessage Create(
        Guid id,
        Guid? organizationId,
        string messageType,
        string payloadJson,
        DateTimeOffset createdAtUtc,
        string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new ArgumentException("Message type is required.", nameof(messageType));
        }

        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        return new OutboxMessage
        {
            Id = id,
            OrganizationId = organizationId,
            MessageType = messageType,
            PayloadJson = payloadJson,
            Status = OutboxMessageStatus.Pending,
            CreatedAtUtc = createdAtUtc,
            NextAttemptAtUtc = createdAtUtc,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Claims the message for one dispatcher for the lease duration.</summary>
    public void Lease(string owner, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        Status = OutboxMessageStatus.Processing;
        LeaseOwner = owner;
        LeaseExpiresAtUtc = nowUtc.Add(leaseDuration);
    }

    public void MarkProcessed(DateTimeOffset nowUtc)
    {
        Status = OutboxMessageStatus.Processed;
        ProcessedAtUtc = nowUtc;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one with exponential backoff plus jitter,
    /// or dead-letters the message once <paramref name="maxAttempts"/> is reached (ADR-0011).
    /// Jitter matters: without it, a provider outage makes every message retry in lockstep.
    /// </summary>
    public void MarkFailed(
        DateTimeOffset nowUtc,
        string error,
        int maxAttempts,
        TimeSpan baseBackoff,
        TimeSpan maxBackoff,
        double jitterFactor)
    {
        AttemptCount++;
        LastError = Truncate(error, 4000);
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;

        if (AttemptCount >= maxAttempts)
        {
            Status = OutboxMessageStatus.DeadLettered;
            return;
        }

        Status = OutboxMessageStatus.Failed;
        NextAttemptAtUtc = nowUtc.Add(ComputeBackoff(AttemptCount, baseBackoff, maxBackoff, jitterFactor));
    }

    /// <summary>
    /// Backoff for the given attempt: <c>base * 2^(attempt-1)</c>, capped, then scaled by a random
    /// factor in <c>[1 - jitter, 1 + jitter]</c>.
    /// </summary>
    public static TimeSpan ComputeBackoff(int attempt, TimeSpan baseBackoff, TimeSpan maxBackoff, double jitterFactor)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt must be at least 1.");
        }

        if (jitterFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFactor), jitterFactor, "Jitter must be between 0 and 1.");
        }

        var exponent = Math.Min(attempt - 1, 30);
        var scaled = baseBackoff.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(scaled, maxBackoff.TotalMilliseconds);

        if (jitterFactor == 0d)
        {
            return TimeSpan.FromMilliseconds(capped);
        }

        var jitter = 1d + ((Random.Shared.NextDouble() * 2d * jitterFactor) - jitterFactor);
        return TimeSpan.FromMilliseconds(Math.Max(0d, capped * jitter));
    }

    /// <summary>Returns a dead-lettered message to the queue after a human has fixed the cause.</summary>
    public void Requeue(DateTimeOffset nowUtc)
    {
        Status = OutboxMessageStatus.Pending;
        AttemptCount = 0;
        NextAttemptAtUtc = nowUtc;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}

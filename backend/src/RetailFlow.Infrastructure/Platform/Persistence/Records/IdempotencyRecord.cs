namespace RetailFlow.Infrastructure.Platform.Persistence.Records;

/// <summary>Lifecycle of an idempotency reservation (ADR-0010).</summary>
public enum IdempotencyRecordStatus
{
    /// <summary>Reserved by a call that has not committed yet.</summary>
    InProgress = 0,

    /// <summary>Finished successfully; the stored response is replayable.</summary>
    Completed = 1,
}

/// <summary>
/// Remembers the outcome of a retryable operation so a repeat of the same request returns the
/// original result instead of creating a second sale, payment or return (ADR-0010).
/// </summary>
/// <remarks>
/// Only successful outcomes are kept. A rejected request rolls back with its reservation, so the
/// client can correct the cause and retry with the same key.
/// </remarks>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The client-supplied key, unique per organization.</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>Operation the key was used for, so the same key cannot cross operations.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the canonical request, to detect a key reused with a different body.</summary>
    public string RequestHash { get; private set; } = string.Empty;

    public IdempotencyRecordStatus Status { get; private set; }

    public int? HttpStatusCode { get; private set; }

    public string? ResponseJson { get; private set; }

    /// <summary>Resource the original call produced, for support lookups.</summary>
    public Guid? ResourceId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>When the purge job may remove this record (NFR-09).</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public static IdempotencyRecord Begin(
        Guid id,
        Guid organizationId,
        string key,
        string endpoint,
        string requestHash,
        DateTimeOffset nowUtc,
        TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "Retention must be positive.");
        }

        return new IdempotencyRecord
        {
            Id = id,
            OrganizationId = organizationId,
            Key = key,
            Endpoint = endpoint,
            RequestHash = requestHash,
            Status = IdempotencyRecordStatus.InProgress,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.Add(retention),
        };
    }

    public void Complete(DateTimeOffset nowUtc, int httpStatusCode, string? responseJson, Guid? resourceId)
    {
        Status = IdempotencyRecordStatus.Completed;
        HttpStatusCode = httpStatusCode;
        ResponseJson = responseJson;
        ResourceId = resourceId;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>
    /// True when a reservation has been in progress longer than the given window, which means the
    /// process handling it died before committing. Such a record is safe to take over because the
    /// business transaction it belonged to was rolled back by the database.
    /// </summary>
    public bool IsStaleReservation(DateTimeOffset nowUtc, TimeSpan staleAfter) =>
        Status == IdempotencyRecordStatus.InProgress && nowUtc - CreatedAtUtc > staleAfter;

    /// <summary>Re-reserves a stale record for a new attempt.</summary>
    public void TakeOver(DateTimeOffset nowUtc, string requestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        Status = IdempotencyRecordStatus.InProgress;
        RequestHash = requestHash;
        CreatedAtUtc = nowUtc;
        HttpStatusCode = null;
        ResponseJson = null;
        ResourceId = null;
        CompletedAtUtc = null;
    }
}

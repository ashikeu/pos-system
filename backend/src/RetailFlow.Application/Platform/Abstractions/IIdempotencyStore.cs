namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>What a lookup of an idempotency key found.</summary>
public enum IdempotencyOutcome
{
    /// <summary>The key has not been used: the operation should run.</summary>
    New = 0,

    /// <summary>The key was used with the same request and finished: replay the stored response.</summary>
    Completed = 1,

    /// <summary>
    /// The key is reserved by a call that has not finished. The client should retry with the same
    /// key rather than generating a new one (ADR-0010).
    /// </summary>
    InProgress = 2,

    /// <summary>The key was used for a different request body: the client has a bug.</summary>
    Mismatch = 3,
}

/// <summary>Result of looking up an idempotency key.</summary>
/// <param name="Outcome">What was found.</param>
/// <param name="HttpStatusCode">Status code of the stored response, when completed.</param>
/// <param name="ResponseJson">Serialized response of the original call, when completed.</param>
/// <param name="ResourceId">Identifier of the resource the original call created, when known.</param>
public sealed record IdempotencyLookup(
    IdempotencyOutcome Outcome,
    int? HttpStatusCode = null,
    string? ResponseJson = null,
    Guid? ResourceId = null)
{
    public static IdempotencyLookup New { get; } = new(IdempotencyOutcome.New);
}

/// <summary>
/// Stores the outcome of retryable operations so a lost acknowledgement can never create a second
/// sale, payment or return (ADR-0010, FR-OFF-06).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Looks up a key for the given endpoint and request fingerprint. Called before the business
    /// transaction opens.
    /// </summary>
    Task<IdempotencyLookup> FindAsync(
        Guid organizationId,
        string key,
        string endpoint,
        string requestHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves the key as in progress. Called inside the business transaction, so the reservation
    /// and the business change share one fate. Returns the existing lookup when another caller
    /// won the race on the unique key.
    /// </summary>
    Task<IdempotencyLookup> BeginAsync(
        Guid organizationId,
        string key,
        string endpoint,
        string requestHash,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the response against the key, inside the same transaction as the business change,
    /// so a replay can return exactly what the first call returned.
    /// </summary>
    Task CompleteAsync(
        Guid organizationId,
        string key,
        int httpStatusCode,
        string? responseJson,
        Guid? resourceId,
        CancellationToken cancellationToken = default);
}

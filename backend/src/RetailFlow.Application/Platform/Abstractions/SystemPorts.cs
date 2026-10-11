namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>
/// The system clock. Business code never calls <see cref="DateTime.Now"/>: a POS reconciles money
/// against time, so the clock has to be substitutable in tests and consistently UTC (ADR-0007).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Creates entity identifiers. Behind the port so a deterministic generator can be used in tests
/// and so the UUIDv7 policy (ADR-0005) lives in one place.
/// </summary>
public interface IGuidGenerator
{
    Guid NewId();

    /// <summary>Creates an identifier stamped with a specific instant, for backdated records.</summary>
    Guid NewId(DateTimeOffset timestamp);
}

/// <summary>
/// Transaction boundary for a unit of work. Implementations wrap the operation in the provider's
/// execution strategy so a deadlock or transient fault retries the whole transaction rather than
/// committing half of it (ADR-0003, ADR-0008).
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="operation"/> inside a database transaction, committing on success and
    /// rolling back on any exception. The operation may be invoked more than once if a transient
    /// fault triggers a retry, so it must not depend on state mutated by a previous attempt.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>Flushes tracked changes. Inside a transaction this does not commit.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Takes a write lock on specific rows before they are read and updated, for the cases where
/// optimistic concurrency is not enough — the last unit of stock sold on two terminals at once,
/// document number allocation, credit limits and cash sessions (ADR-0008).
/// </summary>
public interface IRowLocker
{
    /// <summary>
    /// Locks the rows of <typeparamref name="TEntity"/> with the given keys for update, in key
    /// order to keep lock acquisition deadlock-free. Must be called inside a transaction.
    /// </summary>
    Task LockForUpdateAsync<TEntity>(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
        where TEntity : class;
}

/// <summary>
/// Read-through cache for reference data. Never used for stock, credit or payment state: a stale
/// answer there would authorise the wrong thing (NFR-02, ADR-0011).
/// </summary>
public interface ICacheService
{
    Task<TValue?> GetAsync<TValue>(string key, CancellationToken cancellationToken = default)
        where TValue : class;

    Task SetAsync<TValue>(
        string key,
        TValue value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class;

    Task<TValue> GetOrAddAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class;

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mutual exclusion across processes, for work that must not run twice concurrently (outbox
/// leases, scheduled jobs). The in-memory implementation is single-node only; Redis is used when
/// more than one instance runs (ADR-0011).
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire the named lock. Returns <c>null</c> when it is held elsewhere; dispose
    /// the handle to release it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(
        string name,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

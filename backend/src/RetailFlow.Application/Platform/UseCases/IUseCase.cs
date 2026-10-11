using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// One business operation. There is no mediator: controllers and jobs inject the use case they
/// need, so the call stack stays readable and navigable (ADR-0015). Cross-cutting concerns are
/// applied by explicit decorators registered in a fixed order.
/// </summary>
public interface IUseCase<in TRequest, TResponse>
    where TRequest : notnull
{
    Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A request that may be retried after an unknown outcome and must therefore be de-duplicated by
/// key (ADR-0010). Sales, returns, payments, receipts, transfers, shift closes and every sync
/// operation implement this.
/// </summary>
public interface IIdempotentRequest
{
    /// <summary>
    /// Client-generated key, stable across retries of the same logical operation. For offline
    /// operations this is the device operation id (ADR-0017).
    /// </summary>
    string IdempotencyKey { get; }

    /// <summary>
    /// Status code recorded for a successful first execution, so a replay answers identically.
    /// Operations that create a resource override this with 201.
    /// </summary>
    int SuccessStatusCode => 200;
}

/// <summary>
/// A request whose work must run inside a database transaction. Read-only queries do not implement
/// this, so they are not wrapped in a pointless transaction.
/// </summary>
public interface ITransactionalRequest;

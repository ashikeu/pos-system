using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// Makes a retryable operation safe to repeat (ADR-0010). The lookup happens before the
/// transaction; the reservation, the business work and the stored response all commit together, so
/// a replay returns exactly what the first call returned and a lost acknowledgement can never
/// produce a second sale.
/// </summary>
/// <remarks>
/// This decorator owns the transaction itself rather than sitting outside
/// <see cref="TransactionalUseCase{TRequest, TResponse}"/>: the response snapshot has to be written
/// inside the same transaction as the business change, which two independent decorators cannot
/// guarantee.
/// </remarks>
public sealed class IdempotentUseCase<TRequest, TResponse> : IUseCase<TRequest, TResponse>
    where TRequest : notnull, IIdempotentRequest
{
    private readonly IUseCase<TRequest, TResponse> _inner;
    private readonly IIdempotencyStore _store;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IdempotentUseCase<TRequest, TResponse>> _logger;
    private readonly IdempotencyOptions _options;

    public IdempotentUseCase(
        IUseCase<TRequest, TResponse> inner,
        IIdempotencyStore store,
        IUnitOfWork unitOfWork,
        ITenantContext tenant,
        ILogger<IdempotentUseCase<TRequest, TResponse>> logger,
        IdempotencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _store = store;
        _unitOfWork = unitOfWork;
        _tenant = tenant;
        _logger = logger;
        _options = options;
    }

    public async Task<Result<TResponse>> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Error.Validation(
                "platform.idempotency_key_required",
                "This operation requires an Idempotency-Key.");
        }

        var organizationId = _tenant.OrganizationId;
        var endpoint = typeof(TRequest).FullName ?? typeof(TRequest).Name;
        var requestHash = RequestFingerprint.Compute(request);

        var existing = await _store
            .FindAsync(organizationId, request.IdempotencyKey, endpoint, requestHash, cancellationToken)
            .ConfigureAwait(false);

        if (TryReplay(existing, request.IdempotencyKey, out var replayed))
        {
            return replayed;
        }

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    var reservation = await _store.BeginAsync(
                        organizationId,
                        request.IdempotencyKey,
                        endpoint,
                        requestHash,
                        _options.Retention,
                        ct).ConfigureAwait(false);

                    if (TryReplay(reservation, request.IdempotencyKey, out var raced))
                    {
                        return raced;
                    }

                    var result = await _inner.HandleAsync(request, ct).ConfigureAwait(false);

                    if (result.IsFailure)
                    {
                        // Roll back the reservation as well: a rejected request must be retryable
                        // with the same key once the client has fixed the cause.
                        throw new BusinessFailureRollbackException(result.Error);
                    }

                    await _store.CompleteAsync(
                        organizationId,
                        request.IdempotencyKey,
                        request.SuccessStatusCode,
                        PlatformJson.Serialize(result.Value),
                        TryGetResourceId(result.Value),
                        ct).ConfigureAwait(false);

                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessFailureRollbackException ex)
        {
            return Result.Failure<TResponse>(ex.Error);
        }
    }

    private bool TryReplay(IdempotencyLookup lookup, string key, out Result<TResponse> result)
    {
        switch (lookup.Outcome)
        {
            case IdempotencyOutcome.Completed:
                PlatformLog.IdempotencyReplay(_logger, key, typeof(TRequest).Name);

                if (lookup.ResponseJson is null)
                {
                    result = Result.Success(default(TResponse)!);
                    return true;
                }

                var stored = PlatformJson.Deserialize<TResponse>(lookup.ResponseJson);
                if (stored is null)
                {
                    PlatformLog.IdempotencyReplayUnreadable(_logger, key, typeof(TRequest).Name);
                    result = Error.Conflict(
                        "platform.idempotency_replay_failed",
                        "The stored response for this key could not be read. Contact support with the key.");
                    return true;
                }

                result = Result.Success(stored);
                return true;

            case IdempotencyOutcome.InProgress:
                PlatformLog.IdempotencyInProgress(_logger, key, typeof(TRequest).Name);

                result = Error.Conflict(
                    "platform.operation_in_progress",
                    "An identical request is still being processed. Retry with the same Idempotency-Key.");
                return true;

            case IdempotencyOutcome.Mismatch:
                PlatformLog.IdempotencyKeyReuse(_logger, key, typeof(TRequest).Name);

                result = Error.Conflict(
                    "platform.idempotency_key_reuse",
                    "This Idempotency-Key was already used for a different request.");
                return true;

            case IdempotencyOutcome.New:
            default:
                result = default!;
                return false;
        }
    }

    // Lets the store record which resource the operation produced, so support can trace a replay
    // back to the sale or return it created without parsing the response snapshot.
    private static Guid? TryGetResourceId(TResponse? response) => response switch
    {
        null => null,
        Guid id => id,
        _ => response.GetType().GetProperty("Id")?.GetValue(response) as Guid?,
    };
}

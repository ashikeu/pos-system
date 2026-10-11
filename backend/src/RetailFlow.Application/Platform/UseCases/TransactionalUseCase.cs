using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// Runs the handler inside one database transaction and flushes on success. A business failure
/// (a <see cref="Result"/> failure, not an exception) also rolls back, because a rejected sale must
/// leave behind no stock movement, no payment and no consumed document number.
/// </summary>
public sealed class TransactionalUseCase<TRequest, TResponse> : IUseCase<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IUseCase<TRequest, TResponse> _inner;
    private readonly IUnitOfWork _unitOfWork;

    public TransactionalUseCase(IUseCase<TRequest, TResponse> inner, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        _inner = inner;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<TResponse>> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    var result = await _inner.HandleAsync(request, ct).ConfigureAwait(false);

                    if (result.IsFailure)
                    {
                        // The only way to abandon a transaction is to throw; the exception is
                        // caught below and turned back into the original business failure.
                        throw new BusinessFailureRollbackException(result.Error);
                    }

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
}

/// <summary>
/// Internal signal that rolls a transaction back when the handler returned a business failure. It
/// never leaves the decorator that threw it.
/// </summary>
public sealed class BusinessFailureRollbackException : Exception
{
    public BusinessFailureRollbackException()
        : this(new Error("platform.operation_failed", "The operation failed."))
    {
    }

    public BusinessFailureRollbackException(string message)
        : this(new Error("platform.operation_failed", message))
    {
    }

    public BusinessFailureRollbackException(string message, Exception innerException)
        : base(message, innerException) =>
        Error = new Error("platform.operation_failed", message);

    public BusinessFailureRollbackException(Error error)
        : base(error?.Message ?? "The operation failed.") =>
        Error = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>The business failure that caused the rollback.</summary>
    public Error Error { get; }
}

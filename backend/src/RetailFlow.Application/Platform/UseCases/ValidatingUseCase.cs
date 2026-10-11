using FluentValidation;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// Runs every registered validator for the request before the handler sees it, and turns failures
/// into a single validation <see cref="Error"/> with per-field messages (ADR-0014). Outermost but
/// one, so nothing invalid ever opens a transaction.
/// </summary>
public sealed class ValidatingUseCase<TRequest, TResponse> : IUseCase<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IUseCase<TRequest, TResponse> _inner;
    private readonly IReadOnlyCollection<IValidator<TRequest>> _validators;

    public ValidatingUseCase(IUseCase<TRequest, TResponse> inner, IEnumerable<IValidator<TRequest>> validators)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(validators);

        _inner = inner;
        _validators = validators.ToArray();
    }

    public async Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        if (_validators.Count == 0)
        {
            return await _inner.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
            {
                failures.AddRange(result.Errors);
            }
        }

        if (failures.Count == 0)
        {
            return await _inner.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var fieldErrors = failures
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => f.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return Error.Validation(
            "request.validation_failed",
            "The request is not valid.",
            fieldErrors);
    }
}

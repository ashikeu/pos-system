namespace RetailFlow.Domain.Platform;

/// <summary>
/// Outcome of an operation that can fail for business reasons. Expected failures travel as values
/// so a caller cannot ignore them by accident; exceptions stay reserved for bugs and infrastructure
/// faults (ADR-0015).
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error is null)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        ErrorOrNull = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>The failure, or null when the operation succeeded.</summary>
    public Error? ErrorOrNull { get; }

    /// <summary>The failure. Throws on a successful result, which would be a caller bug.</summary>
    public Error Error => ErrorOrNull
        ?? throw new InvalidOperationException("A successful result has no error.");

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.FromValue(value);

    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.FromError(error);
}

/// <summary>An operation outcome that carries a value on success.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue? value, bool isSuccess, Error? error)
        : base(isSuccess, error) => _value = value;

    /// <summary>The value. Throws on a failed result, which would be a caller bug.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value: " + Error.Code);

    public static implicit operator Result<TValue>(TValue value) => FromValue(value);

    public static implicit operator Result<TValue>(Error error) => FromError(error);

    internal static Result<TValue> FromValue(TValue value) => new(value, true, null);

    internal static Result<TValue> FromError(Error error) => new(default, false, error);

    /// <summary>Projects the value of a successful result, passing a failure through untouched.</summary>
    public Result<TNext> Map<TNext>(Func<TValue, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result<TNext>.FromValue(map(Value)) : Result<TNext>.FromError(Error);
    }
}

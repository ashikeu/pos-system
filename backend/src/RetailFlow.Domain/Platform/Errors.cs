namespace RetailFlow.Domain.Platform;

/// <summary>
/// Classification of a failure, mapped to an HTTP status by the API layer (ADR-0014). The code is
/// the stable contract clients branch on; the message is for humans and may be localised.
/// </summary>
public enum ErrorType
{
    /// <summary>Input failed validation. HTTP 400.</summary>
    Validation = 0,

    /// <summary>The caller is not authenticated. HTTP 401.</summary>
    Unauthenticated = 1,

    /// <summary>The caller lacks a permission, branch grant or tenant claim. HTTP 403.</summary>
    Forbidden = 2,

    /// <summary>The addressed resource does not exist in this tenant. HTTP 404.</summary>
    NotFound = 3,

    /// <summary>A business rule or concurrency check rejected the operation. HTTP 409.</summary>
    Conflict = 4,

    /// <summary>Syntactically valid but semantically impossible. HTTP 422.</summary>
    Unprocessable = 5,

    /// <summary>Too many requests. HTTP 429.</summary>
    RateLimited = 6,

    /// <summary>A dependency (database, provider) is unavailable. HTTP 503.</summary>
    Unavailable = 7,

    /// <summary>
    /// An unexpected fault. HTTP 500. Never used to report a business outcome: if a condition is
    /// foreseeable, it deserves its own code and status.
    /// </summary>
    Internal = 8,
}

/// <summary>A failure with a stable code, a human-readable message and optional field details.</summary>
public sealed record Error
{
    public Error(
        string code,
        string message,
        ErrorType type = ErrorType.Conflict,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Error code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Error message is required.", nameof(message));
        }

        Code = code;
        Message = message;
        Type = type;
        FieldErrors = fieldErrors;
    }

    /// <summary>Dotted, stable identifier such as <c>inventory.insufficient_stock</c>.</summary>
    public string Code { get; }

    public string Message { get; }

    public ErrorType Type { get; }

    /// <summary>Per-field messages for validation failures, keyed by request path.</summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }

    public static Error Validation(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(code, message, ErrorType.Validation, fieldErrors);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    public static Error Internal(string code, string message) => new(code, message, ErrorType.Internal);

    public override string ToString() => Code + ": " + Message;
}

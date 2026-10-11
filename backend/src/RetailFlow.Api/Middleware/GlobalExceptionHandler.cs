using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Api.Configuration;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Api.Middleware;

/// <summary>
/// Last line of defence: converts an unhandled exception into a ProblemDetails response without
/// leaking internals (ADR-0014). Tenant violations and concurrency conflicts get specific,
/// actionable answers; anything else is a 500 with a correlation id the support team can trace.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var error = Map(exception);

        if (error.Type is ErrorType.Conflict or ErrorType.Unavailable or ErrorType.Forbidden
            or ErrorType.Unprocessable)
        {
            ApiLog.RequestRejected(_logger, exception, httpContext.Request.Path, error.Code);
        }
        else
        {
            ApiLog.RequestFailed(_logger, exception, httpContext.Request.Path);
        }

        var problem = ApiProblemDetails.From(error, httpContext);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static Error Map(Exception exception) => exception switch
    {
        // A cross-tenant write or read attempt. The caller is told nothing about the other tenant.
        TenantMismatchException => Error.Forbidden(
            "platform.tenant_mismatch",
            "The requested record does not belong to your organization."),

        MissingTenantContextException => Error.Forbidden(
            "platform.tenant_context_missing",
            "The request has no organization context."),

        DbUpdateConcurrencyException => Error.Conflict(
            "platform.concurrent_update",
            "Someone else changed this record while you were working on it. Reload and try again."),

        DbUpdateException => Error.Conflict(
            "platform.write_conflict",
            "The change could not be saved because it conflicts with existing data."),

        OperationCanceledException or TaskCanceledException => Error.Unavailable(
            "platform.request_cancelled",
            "The request was cancelled before it completed."),

        TimeoutException => Error.Unavailable(
            "platform.timeout",
            "The operation timed out. If this was a sale, retry with the same Idempotency-Key."),

        DomainException domain => Error.Unprocessable("domain.invariant_violated", domain.Message),

        _ => Error.Internal(
            "platform.unexpected_error",
            "An unexpected error occurred. Quote the correlation id when reporting it."),
    };
}

/// <summary>Source-generated log messages for the API layer.</summary>
public static partial class ApiLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Error,
        Message = "Unhandled exception while processing {Path}")]
    public static partial void RequestFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Request to {Path} rejected as {ErrorCode}")]
    public static partial void RequestRejected(ILogger logger, Exception exception, string path, string errorCode);
}

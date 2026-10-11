using Microsoft.AspNetCore.Mvc;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Api.Configuration;

/// <summary>
/// Translates domain failures into RFC 9457 responses (ADR-0014). One place owns the status-code
/// mapping, so an offline client can rely on a stable <c>code</c> and the same HTTP status for the
/// same business condition across every endpoint.
/// </summary>
public static class ApiProblemDetails
{
    private const string TypeBase = "https://retailflow.dev/errors/";

    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorType.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorType.Internal => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static ProblemDetails From(Error error, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(httpContext);

        var status = StatusFor(error.Type);

        var problem = new ProblemDetails
        {
            Type = TypeBase + error.Code.Replace('.', '-').Replace('_', '-'),
            Title = TitleFor(error.Type),
            Status = status,
            Detail = error.Message,
            Instance = httpContext.Request.Path,
        };

        problem.Extensions["code"] = error.Code;
        problem.Extensions["correlationId"] = httpContext.TraceIdentifier;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (error.FieldErrors is { Count: > 0 })
        {
            problem.Extensions["errors"] = error.FieldErrors;
        }

        return problem;
    }

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "Invalid request",
        ErrorType.Unauthenticated => "Authentication required",
        ErrorType.Forbidden => "Not permitted",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict",
        ErrorType.Unprocessable => "Cannot be processed",
        ErrorType.RateLimited => "Too many requests",
        ErrorType.Unavailable => "Service unavailable",
        ErrorType.Internal => "Unexpected error",
        _ => "Unexpected error",
    };
}

/// <summary>Helpers that turn a <see cref="Result"/> into an HTTP response in one line.</summary>
public static class ResultExtensions
{
    public static IActionResult ToActionResult(this Result result, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(controller);

        return result.IsSuccess
            ? controller.NoContent()
            : Problem(result.Error, controller);
    }

    public static IActionResult ToActionResult<TValue>(this Result<TValue> result, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(controller);

        return result.IsSuccess
            ? controller.Ok(result.Value)
            : Problem(result.Error, controller);
    }

    public static IActionResult ToCreatedResult<TValue>(
        this Result<TValue> result,
        ControllerBase controller,
        string routeName,
        object routeValues)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(controller);

        return result.IsSuccess
            ? controller.CreatedAtRoute(routeName, routeValues, result.Value)
            : Problem(result.Error, controller);
    }

    private static ObjectResult Problem(Error error, ControllerBase controller)
    {
        var problem = ApiProblemDetails.From(error, controller.HttpContext);
        return new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
    }
}

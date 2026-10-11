using RetailFlow.Application.Platform.Abstractions;
using Serilog.Context;

namespace RetailFlow.Api.Middleware;

/// <summary>
/// Establishes the correlation id for the request and records the caller's transport details
/// (ADR-0014). A terminal sends its own id so one complaint ("this sale printed twice") can be
/// followed across the device, the API, the workers and the audit log.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _headerName;

    public CorrelationIdMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(configuration);

        _next = next;
        _headerName = configuration["Api:CorrelationIdHeader"] ?? "X-Correlation-Id";
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMutableCorrelationContext correlation,
        IMutableRequestMetadata requestMetadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(requestMetadata);

        var incoming = context.Request.Headers[_headerName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            correlation.Set(incoming);
        }

        var correlationId = correlation.CorrelationId;

        requestMetadata.Set(
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.FirstOrDefault());

        // Echoed before the response starts, so even a failing request can be traced by the client.
        context.Response.Headers[_headerName] = correlationId;
        context.TraceIdentifier = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}

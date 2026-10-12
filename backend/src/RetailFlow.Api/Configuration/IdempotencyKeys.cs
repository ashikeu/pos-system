namespace RetailFlow.Api.Configuration;

/// <summary>
/// Reads the <c>Idempotency-Key</c> header (ADR-0010). The header is the documented transport for
/// the key; the request body carries the same field so an offline client can queue the key with the
/// operation it belongs to. The header wins when both are present, because it is what a retrying
/// HTTP client sets.
/// </summary>
public static class IdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>The key from the header, or <c>null</c> when the caller did not send one.</summary>
    public static string? FromHeader(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Headers[HeaderName].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The header value when present, otherwise what the body supplied.</summary>
    public static string Resolve(HttpRequest request, string? fromBody) =>
        FromHeader(request) ?? fromBody ?? string.Empty;
}

namespace RetailFlow.Application.Platform.Contracts;

/// <summary>
/// Offset paging for admin lists (ADR-0014). Sync pulls and large exports use a keyset cursor
/// instead, because an offset deep into a growing table both drifts and gets slow.
/// </summary>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page, capped at <see cref="MaxPageSize"/>.</param>
/// <param name="SortBy">
/// A sort key from the endpoint's allow-list. Free-form field names are never accepted: they let a
/// caller sort by a column with no index and turn a list screen into a table scan.
/// </param>
/// <param name="Descending">Whether to sort descending.</param>
public sealed record PageRequest(int Page = 1, int PageSize = 50, string? SortBy = null, bool Descending = false)
{
    public const int MaxPageSize = 200;

    public const int DefaultPageSize = 50;

    /// <summary>The page number, clamped to 1 or more.</summary>
    public int SafePage => Page < 1 ? 1 : Page;

    /// <summary>The page size, clamped into 1…<see cref="MaxPageSize"/>.</summary>
    public int SafePageSize => PageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => PageSize,
    };

    public int Skip => (SafePage - 1) * SafePageSize;
}

/// <summary>One page of results, with enough information to render a pager.</summary>
/// <param name="Items">The rows on this page.</param>
/// <param name="Page">The page returned.</param>
/// <param name="PageSize">The page size used.</param>
/// <param name="TotalCount">Total matching rows.</param>
public sealed record PagedResult<TItem>(
    IReadOnlyList<TItem> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasMore => Page < TotalPages;
}

/// <summary>Builders for <see cref="PagedResult{TItem}"/>.</summary>
public static class PagedResult
{
    /// <summary>An empty page that still reflects the request, so a pager renders sensibly.</summary>
    public static PagedResult<TItem> Empty<TItem>(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PagedResult<TItem>([], request.SafePage, request.SafePageSize, 0);
    }
}

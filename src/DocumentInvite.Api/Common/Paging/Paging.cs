using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Common.Paging;

/// <summary>The query-string contract shared by every endpoint that returns a collection.</summary>
public interface IPagedQuery
{
    /// <summary>1-based page number. Defaults to 1.</summary>
    int Page { get; }

    /// <summary>Rows per page. Defaults to 25, at most 100.</summary>
    int PageSize { get; }
}

public static class PagingDefaults
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
}

/// <summary>A pagination-only query, for endpoints that have no filters.</summary>
public sealed record PageQuery(
    int Page = PagingDefaults.DefaultPage,
    int PageSize = PagingDefaults.DefaultPageSize) : IPagedQuery;

/// <summary>The response shape for every collection: the page of rows plus the total row count.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// The one validator for page and pageSize. Query validators include it with
/// <c>Include(new PagedQueryValidator&lt;TheirQuery&gt;())</c>.
/// </summary>
public class PagedQueryValidator<T> : AbstractValidator<T> where T : IPagedQuery
{
    public PagedQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PagingDefaults.MaxPageSize);
    }
}

public sealed class PageQueryValidator : PagedQueryValidator<PageQuery>;

public static class PagingExtensions
{
    /// <summary>Number of rows to skip for a page. Computed in 64 bits so very large pages cannot overflow.</summary>
    public static int Offset(int page, int pageSize) =>
        (int)Math.Min((long)(Math.Max(page, 1) - 1) * pageSize, int.MaxValue);

    /// <summary>The number of pages for a total (always at least 1).</summary>
    public static int PageCount(int total, int pageSize) => Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

    /// <summary>
    /// Counts the rows and reads one page. The query must already carry a deterministic order that ends
    /// in a unique key, so consecutive pages never overlap or skip rows.
    /// </summary>
    public static async Task<PagedResponse<TResult>> ToPagedResponseAsync<TSource, TResult>(
        this IOrderedQueryable<TSource> ordered,
        IPagedQuery query,
        Func<TSource, TResult> map,
        CancellationToken ct)
    {
        var total = await ordered.CountAsync(ct);
        var rows = await ordered.Skip(Offset(query.Page, query.PageSize)).Take(query.PageSize).ToListAsync(ct);
        return new PagedResponse<TResult>(rows.Select(map).ToList(), total, query.Page, query.PageSize);
    }
}

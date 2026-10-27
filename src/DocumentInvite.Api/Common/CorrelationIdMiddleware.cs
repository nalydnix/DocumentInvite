using System.Text.RegularExpressions;
using Serilog.Context;

namespace DocumentInvite.Api.Common;

/// <summary>
/// Reads <c>X-Correlation-Id</c> (or generates one), echoes it in the response and
/// adds it to the log context.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        var id = ValidId().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}

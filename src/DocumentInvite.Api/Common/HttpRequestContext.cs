using System.Security.Claims;

namespace DocumentInvite.Api.Common;

public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    private HttpContext? Http => accessor.HttpContext;

    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    public string CorrelationId => Http?.Items[CorrelationIdMiddleware.ItemKey] as string ?? string.Empty;

    public ActorType ActorType => Http?.User.Identity?.IsAuthenticated == true ? ActorType.User : ActorType.Anonymous;

    public Guid? ActorId
    {
        get
        {
            var value = Http?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }
}

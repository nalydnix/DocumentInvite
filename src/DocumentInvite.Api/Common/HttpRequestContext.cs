using System.Security.Claims;

namespace DocumentInvite.Api.Common;

public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    /// <summary>Set by the job API key filter: the caller is the platform's own scheduler, not a person.</summary>
    public const string SystemActorItemKey = "DocumentInvite.SystemActor";

    /// <summary>Claim carried by portal session tokens: the contact the session belongs to.</summary>
    public const string PartyContactClaim = "contactId";

    /// <summary>Set while a portal session is being created: the party the request acts for.</summary>
    public const string PartyActorItemKey = "DocumentInvite.PartyActor";

    private HttpContext? Http => accessor.HttpContext;

    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    public string CorrelationId => Http?.Items[CorrelationIdMiddleware.ItemKey] as string ?? string.Empty;

    public ActorType ActorType =>
        PartyContactId is not null ? ActorType.Party
        : Http?.User.Identity?.IsAuthenticated == true ? ActorType.User
        : Http?.Items[SystemActorItemKey] is true ? ActorType.System
        : ActorType.Anonymous;

    public Guid? ActorId =>
        PartyContactId
        ?? (Guid.TryParse(Http?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null);

    public void ActAsParty(Guid contactId)
    {
        if (Http is not null)
        {
            Http.Items[PartyActorItemKey] = contactId;
        }
    }

    private Guid? PartyContactId =>
        Http?.Items[PartyActorItemKey] is Guid pending ? pending
        : Guid.TryParse(Http?.User.FindFirstValue(PartyContactClaim), out var claim) ? claim
        : null;
}

using DocumentInvite.Api.Common;

namespace DocumentInvite.Api.Features.Demo;

/// <summary>Attributes demo seeding to the System actor, so seeded events are not blamed on a person.</summary>
public sealed class SystemRequestContext : IRequestContext
{
    public string? IpAddress => null;
    public string? UserAgent => null;
    public string CorrelationId => "demo-seed";
    public ActorType ActorType => ActorType.System;
    public Guid? ActorId => null;

    public void ActAsParty(Guid contactId)
    {
    }
}

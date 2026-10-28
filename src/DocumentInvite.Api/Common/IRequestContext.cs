namespace DocumentInvite.Api.Common;

/// <summary>Per-request facts that the audit trail and logging use.</summary>
public interface IRequestContext
{
    string? IpAddress { get; }
    string? UserAgent { get; }
    string CorrelationId { get; }
    ActorType ActorType { get; }
    Guid? ActorId { get; }

    /// <summary>
    /// Attributes the rest of this request to a party (by contact id). Used while a portal session is
    /// being created, before the caller holds a session token.
    /// </summary>
    void ActAsParty(Guid contactId);
}

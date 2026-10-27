namespace DocumentInvite.Api.Common;

/// <summary>Per-request facts that the audit trail and logging use.</summary>
public interface IRequestContext
{
    string? IpAddress { get; }
    string? UserAgent { get; }
    string CorrelationId { get; }
    ActorType ActorType { get; }
    Guid? ActorId { get; }
}

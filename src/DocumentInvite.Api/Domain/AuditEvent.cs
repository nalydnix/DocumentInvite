using DocumentInvite.Api.Common;

namespace DocumentInvite.Api.Domain;

/// <summary>
/// One entry of the append-only, hash-chained audit trail. Rows are written only through
/// <c>IAuditLog</c> and never modified or deleted. Details hold ids, enum values and counts only.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public byte[] PrevHash { get; set; } = [];
    public byte[] Hash { get; set; } = [];
}

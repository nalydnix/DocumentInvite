namespace DocumentInvite.Api.Domain;

/// <summary>Records that one staff user has read one outbox email. No row means unread for that user.</summary>
public class OutboxEmailRead
{
    public Guid OutboxEmailId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ReadAt { get; set; }
}

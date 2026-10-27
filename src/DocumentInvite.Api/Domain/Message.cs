using System.Text.Json.Serialization;

namespace DocumentInvite.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageAuthorType
{
    Team,
    Party,

    /// <summary>Written by the application itself, for example when an item is rejected.</summary>
    System,
}

/// <summary>A message on an invite's thread. Internal notes are never returned to the portal.</summary>
public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InviteId { get; set; }
    public MessageAuthorType AuthorType { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

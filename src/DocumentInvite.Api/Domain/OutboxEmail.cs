namespace DocumentInvite.Api.Domain;

public enum EmailProvider
{
    Demo,
    Resend,
}

/// <summary>A copy of every email the system sends, whichever provider delivers it.</summary>
public class OutboxEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public EmailProvider Provider { get; set; }
    public string? ProviderMessageId { get; set; }

    /// <summary>Why the provider did not deliver the email. Never holds addresses or message content.</summary>
    public string? DeliveryError { get; set; }
}

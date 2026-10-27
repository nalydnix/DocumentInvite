namespace DocumentInvite.Api.Features.Outbox;

/// <summary>An email to send. Bodies come from <see cref="EmailTemplates"/>.</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody)
{
    public static EmailMessage From(string to, EmailContent content) =>
        new(to, content.Subject, content.Html, content.Text);
}

/// <param name="OutboxEmailId">The stored copy. It exists even when delivery failed.</param>
/// <param name="Delivered">True when the provider accepted the email (always true for the demo outbox).</param>
/// <param name="Error">A short reason when delivery failed. Safe to show to staff and to log.</param>
public sealed record EmailSendResult(Guid OutboxEmailId, bool Delivered, string? Error = null);

public interface IEmailSender
{
    /// <summary>
    /// Stores the email in the outbox, then delivers it when the provider is a real one. The outbox
    /// row is added to the shared <c>AppDbContext</c> and saved, so it commits with the caller's
    /// transaction when one is open. A delivery failure is logged and returned, never thrown.
    /// </summary>
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

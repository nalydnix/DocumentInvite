using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;

namespace DocumentInvite.Api.Features.Outbox;

/// <summary>Writes emails to the database only. The default, and what the demo is driven by.</summary>
public sealed class DemoOutboxEmailSender(OutboxStore store) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var email = await store.AddAsync(message, EmailProvider.Demo, cancellationToken);
        return new EmailSendResult(email.Id, Delivered: true);
    }
}

/// <summary>Adds the stored copy of an email. Shared by every <see cref="IEmailSender"/>.</summary>
public sealed class OutboxStore(AppDbContext db, TimeProvider clock)
{
    private const int MaxSubjectLength = 500;

    public async Task<OutboxEmail> AddAsync(EmailMessage message, EmailProvider provider, CancellationToken cancellationToken)
    {
        var email = new OutboxEmail
        {
            To = message.To,
            Subject = Truncate(message.Subject),
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
            CreatedAt = clock.GetUtcNow(),
            Provider = provider,
        };
        db.OutboxEmails.Add(email);
        await db.SaveChangesAsync(cancellationToken);
        return email;
    }

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    private static string Truncate(string subject) =>
        subject.Length <= MaxSubjectLength ? subject : subject[..MaxSubjectLength];
}

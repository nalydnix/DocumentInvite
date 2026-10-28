using System.Globalization;
using System.Net;
using System.Text;

namespace DocumentInvite.Api.Features.Outbox;

public sealed record EmailContent(string Subject, string Html, string Text);

public sealed record InviteEmailModel(string RecipientName, string LoanName, string PortalLink, DateOnly DueDate, int ItemCount);

public sealed record ReminderEmailModel(string RecipientName, string LoanName, string PortalLink, DateOnly DueDate, int OutstandingCount);

public sealed record OverdueEmailModel(string RecipientName, string LoanName, string PortalLink, DateOnly DueDate, int OutstandingCount);

public sealed record ItemRejectedEmailModel(string RecipientName, string LoanName, string ItemName, string Reason, string PortalLink);

public sealed record NewMessageEmailModel(string RecipientName, string LoanName, string PortalLink);

public sealed record NewPartyMessageEmailModel(string RecipientName, string LoanName, string PartyName, string LoanLink);

/// <summary>
/// Pure functions that build the six emails, each with an HTML and a text body that both carry
/// the portal link. Every value is HTML-encoded in the HTML body, and line breaks are removed
/// from subjects. Message text is never included: parties read it in the portal.
/// </summary>
public static class EmailTemplates
{
    public static EmailContent Invite(InviteEmailModel m) => Build(
        $"Documents requested for {m.LoanName}",
        m.RecipientName, m.PortalLink, "Open your secure portal",
        [
            $"You have been asked to provide {Items(m.ItemCount)} for {m.LoanName}.",
            $"Please complete your checklist by {Date(m.DueDate)}. You do not need an account: the secure link below is just for you.",
        ]);

    public static EmailContent Reminder(ReminderEmailModel m) => Build(
        $"Reminder: {Items(m.OutstandingCount)} still needed for {m.LoanName}",
        m.RecipientName, m.PortalLink, "Continue in your portal",
        [
            $"{Items(m.OutstandingCount, capitalise: true)} still outstanding for {m.LoanName}.",
            $"Please complete them by {Date(m.DueDate)}.",
        ]);

    public static EmailContent Overdue(OverdueEmailModel m) => Build(
        $"Overdue: documents for {m.LoanName}",
        m.RecipientName, m.PortalLink, "Complete your checklist now",
        [
            $"Your documents for {m.LoanName} were due on {Date(m.DueDate)} and {Items(m.OutstandingCount)} still outstanding.",
            "Please complete them as soon as you can, or reply to your lender contact if you need more time.",
        ]);

    public static EmailContent ItemRejected(ItemRejectedEmailModel m) => Build(
        $"Action needed on \"{m.ItemName}\" for {m.LoanName}",
        m.RecipientName, m.PortalLink, "Review and resubmit",
        [
            $"Your lender could not accept \"{m.ItemName}\" for {m.LoanName}.",
            $"Reason: {m.Reason}",
            "Please open your portal and submit it again.",
        ]);

    public static EmailContent NewMessage(NewMessageEmailModel m) => Build(
        $"New message about {m.LoanName}",
        m.RecipientName, m.PortalLink, "Read the message",
        [
            $"You have a new message from your lender about {m.LoanName}.",
            "For your security, messages are shown only in your secure portal.",
        ]);

    /// <summary>Tells a staff member that a party replied. The link opens the loan page, which needs a sign-in.</summary>
    public static EmailContent NewPartyMessage(NewPartyMessageEmailModel m) => Build(
        $"New message from {m.PartyName} about {m.LoanName}",
        m.RecipientName, m.LoanLink, "Open the loan",
        [
            $"{m.PartyName} sent you a message about {m.LoanName}.",
            "Sign in and open the loan to read and reply.",
        ],
        footer: "You are receiving this because you created this loan.");

    private static string Items(int count, bool capitalise = false)
    {
        var text = count == 1 ? "1 item is" : $"{count.ToString(CultureInfo.InvariantCulture)} items are";
        if (!capitalise) return count == 1 ? "1 item" : $"{count.ToString(CultureInfo.InvariantCulture)} items";
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    private static string Date(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private static EmailContent Build(
        string subject, string recipientName, string portalLink, string buttonLabel, IReadOnlyList<string> paragraphs,
        string footer = "This link is personal to you. Please do not forward this email.")
    {
        if (!Uri.TryCreate(portalLink, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The portal link must be an absolute http or https URL.", nameof(portalLink));
        }

        var greeting = $"Hello {recipientName},";
        var safeSubject = subject.ReplaceLineEndings(" ").Trim();

        var html = new StringBuilder()
            .Append("<!doctype html><html><body style=\"margin:0;padding:24px;background:#f5f7fa;font-family:Arial,Helvetica,sans-serif;color:#1f2937\">")
            .Append("<table role=\"presentation\" width=\"100%\" style=\"max-width:560px;margin:0 auto;background:#ffffff;border-radius:8px;padding:24px\"><tr><td>")
            .Append("<p style=\"font-size:18px;font-weight:bold;color:#2563eb;margin:0 0 16px\">DocumentInvite</p>")
            .Append($"<p style=\"margin:0 0 12px\">{Encode(greeting)}</p>");
        foreach (var paragraph in paragraphs)
        {
            html.Append($"<p style=\"margin:0 0 12px\">{Encode(paragraph)}</p>");
        }

        var href = Encode(uri.AbsoluteUri);
        html.Append($"<p style=\"margin:20px 0\"><a href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\" ")
            .Append("style=\"background:#2563eb;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:6px;display:inline-block\">")
            .Append(Encode(buttonLabel)).Append("</a></p>")
            .Append("<p style=\"margin:0 0 12px;font-size:13px;color:#6b7280\">If the button does not work, copy this link into your browser:<br>")
            .Append($"<a href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\">{href}</a></p>")
            .Append($"<p style=\"margin:0;font-size:12px;color:#6b7280\">{Encode(footer)}</p>")
            .Append("</td></tr></table></body></html>");

        var text = new StringBuilder().AppendLine(greeting).AppendLine();
        foreach (var paragraph in paragraphs)
        {
            text.AppendLine(paragraph).AppendLine();
        }

        text.AppendLine($"{buttonLabel}: {uri.AbsoluteUri}").AppendLine()
            .AppendLine(footer);

        return new EmailContent(safeSubject, html.ToString(), text.ToString());
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

using DocumentInvite.Api.Common;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Features.Portal;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Messaging;

public sealed record MessageResult<T>(T? Value, bool NotFound, string? Conflict, bool DeliveryFailed)
{
    public static MessageResult<T> Ok(T value) => new(value, false, null, false);
    public static MessageResult<T> Missing() => new(default, true, null, false);
    public static MessageResult<T> Refused(string reason) => new(default, false, reason, false);
    public static MessageResult<T> EmailFailed() => new(default, false, null, true);
}

/// <summary>
/// The thread on an invite. The team and the party read the same thread, except that internal notes are
/// selected only by the staff methods: the portal methods filter them out in the query itself.
/// </summary>
public sealed class MessagingService(
    AppDbContext db,
    IAuditLog audit,
    IEmailSender email,
    IPortalLinkBuilder links,
    IRequestContext context,
    TimeProvider clock)
{
    private const string Stale = "The invite was changed by someone else. Reload it and try again.";

    // ---- staff ----

    /// <summary>The thread, newest first. Internal notes are included only when <paramref name="includeInternal"/> is set.</summary>
    public async Task<MessageResult<PagedResponse<StaffMessageResponse>>> ListForStaffAsync(
        Guid inviteId, bool includeInternal, PageQuery query, CancellationToken ct)
    {
        if (!await db.Invites.AsNoTracking().AnyAsync(i => i.Id == inviteId, ct))
        {
            return MessageResult<PagedResponse<StaffMessageResponse>>.Missing();
        }

        var page = await db.Messages.AsNoTracking()
            .Where(m => m.InviteId == inviteId && (includeInternal || !m.IsInternal))
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .ToPagedResponseAsync(query, m => m, ct);

        var names = await AuthorNamesAsync(page.Items, ct);
        var items = page.Items.Select(m => ToStaff(m, names)).ToList();
        return MessageResult<PagedResponse<StaffMessageResponse>>.Ok(
            new PagedResponse<StaffMessageResponse>(items, page.Total, page.Page, page.PageSize));
    }

    /// <summary>A message to the party. The party is emailed a freshly issued link, since only token hashes are stored.</summary>
    public async Task<MessageResult<StaffMessageResponse>> PostTeamMessageAsync(Guid inviteId, string body, CancellationToken ct)
    {
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
        {
            return MessageResult<StaffMessageResponse>.Missing();
        }

        if (invite.RevokedAt is not null)
        {
            return MessageResult<StaffMessageResponse>.Refused("The party's link is revoked. Reissue the link before sending a message.");
        }

        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == invite.ContactId, ct);
        if (contact.IsPseudonymised)
        {
            return MessageResult<StaffMessageResponse>.Refused("The party's data has been erased, so they cannot be messaged.");
        }

        var loan = await db.Loans.AsNoTracking().SingleAsync(l => l.Id == invite.LoanId, ct);
        var now = clock.GetUtcNow();
        var message = NewMessage(inviteId, MessageAuthorType.Team, body, isInternal: false, now);
        message.AuthorUserId = context.ActorId;
        db.Messages.Add(message);

        // The email carries a new link, so the previous one stops working (as for reminders and rejections).
        var token = PortalTokens.Generate();
        invite.TokenHash = PortalTokens.Hash(token);
        var expiry = InviteService.DefaultExpiry(invite.DueDate);
        var newExpiry = expiry > now ? expiry : now.AddDays(InviteService.LinkValidDaysAfterDueDate);
        if (invite.TokenExpiresAt < newExpiry)
        {
            invite.TokenExpiresAt = newExpiry;
        }

        var content = EmailTemplates.NewMessage(new NewMessageEmailModel(contact.FirstName, loan.Name, links.For(token)));

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync("message.posted", new AuditTarget("invite", inviteId), Details(loan.Id, message.Id, MessageKind.Team), ct);
            var sent = await email.SendAsync(EmailMessage.From(contact.Email, content), ct);
            if (!sent.Delivered)
            {
                await transaction.RollbackAsync(ct);
                return MessageResult<StaffMessageResponse>.EmailFailed();
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return MessageResult<StaffMessageResponse>.Refused(Stale);
        }

        return MessageResult<StaffMessageResponse>.Ok(await ToStaffAsync(message, ct));
    }

    /// <summary>A note for the team only. Nothing is emailed and the party can never read it.</summary>
    public async Task<MessageResult<StaffMessageResponse>> PostInternalNoteAsync(Guid inviteId, string body, CancellationToken ct)
    {
        var loanId = await db.Invites.AsNoTracking().Where(i => i.Id == inviteId).Select(i => (Guid?)i.LoanId).SingleOrDefaultAsync(ct);
        if (loanId is null)
        {
            return MessageResult<StaffMessageResponse>.Missing();
        }

        var message = NewMessage(inviteId, MessageAuthorType.Team, body, isInternal: true, clock.GetUtcNow());
        message.AuthorUserId = context.ActorId;
        db.Messages.Add(message);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await audit.RecordAsync("note.posted", new AuditTarget("invite", inviteId), Details(loanId.Value, message.Id, MessageKind.Internal), ct);
        await transaction.CommitAsync(ct);

        return MessageResult<StaffMessageResponse>.Ok(await ToStaffAsync(message, ct));
    }

    // ---- party ----

    /// <summary>The party's view of the thread, newest first. Internal notes are excluded by the query itself.</summary>
    public async Task<PortalResult<PagedResponse<PortalMessageResponse>>> ListForPartyAsync(
        Guid inviteId, PageQuery query, CancellationToken ct)
    {
        if (!await db.Invites.AsNoTracking().AnyAsync(i => i.Id == inviteId, ct))
        {
            return PortalResult<PagedResponse<PortalMessageResponse>>.Fail(PortalFailure.NotFound);
        }

        var page = await db.Messages.AsNoTracking()
            .Where(m => m.InviteId == inviteId && !m.IsInternal)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .ToPagedResponseAsync(query, m => new PortalMessageResponse(m.Id, m.AuthorType, m.Body, m.CreatedAt), ct);
        return PortalResult<PagedResponse<PortalMessageResponse>>.Ok(page);
    }

    /// <summary>A reply from the party. The loan's creator is emailed a link to the loan page.</summary>
    public async Task<PortalResult<PortalMessageResponse>> PostPartyMessageAsync(Guid inviteId, string body, CancellationToken ct)
    {
        var invite = await db.Invites.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
        {
            return PortalResult<PortalMessageResponse>.Fail(PortalFailure.NotFound);
        }

        if (invite.PrivacyAcceptedAt is null)
        {
            return PortalResult<PortalMessageResponse>.Fail(PortalFailure.PrivacyRequired);
        }

        var loan = await db.Loans.AsNoTracking().SingleAsync(l => l.Id == invite.LoanId, ct);
        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == invite.ContactId, ct);
        var message = NewMessage(inviteId, MessageAuthorType.Party, body, isInternal: false, clock.GetUtcNow());
        db.Messages.Add(message);

        var recipient = loan.CreatedBy is { } creator
            ? await db.Users.AsNoTracking().Where(u => u.Id == creator)
                .Select(u => new { u.Email, u.DisplayName }).SingleOrDefaultAsync(ct)
            : null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await audit.RecordAsync("message.posted", new AuditTarget("invite", inviteId), Details(loan.Id, message.Id, MessageKind.Party), ct);
        if (!string.IsNullOrWhiteSpace(recipient?.Email))
        {
            var content = EmailTemplates.NewPartyMessage(new NewPartyMessageEmailModel(
                recipient.DisplayName, loan.Name, $"{contact.FirstName} {contact.LastName}", links.ForLoan(loan.Id)));
            // A failed delivery is recorded on the stored email; it must not lose the party's message.
            await email.SendAsync(EmailMessage.From(recipient.Email, content), ct);
        }

        await transaction.CommitAsync(ct);
        return PortalResult<PortalMessageResponse>.Ok(
            new PortalMessageResponse(message.Id, message.AuthorType, message.Body, message.CreatedAt));
    }

    // ---- helpers ----

    private static Message NewMessage(Guid inviteId, MessageAuthorType author, string body, bool isInternal, DateTimeOffset now) => new()
    {
        InviteId = inviteId,
        AuthorType = author,
        Body = body.Trim(),
        IsInternal = isInternal,
        CreatedAt = now,
    };

    private static AuditDetails Details(Guid loanId, Guid messageId, MessageKind kind) =>
        AuditDetails.Empty.WithId("loanId", loanId).WithId("messageId", messageId).WithEnum("kind", kind);

    private async Task<Dictionary<Guid, string>> AuthorNamesAsync(IEnumerable<Message> messages, CancellationToken ct)
    {
        var ids = messages.Where(m => m.AuthorUserId is not null).Select(m => m.AuthorUserId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static StaffMessageResponse ToStaff(Message m, IReadOnlyDictionary<Guid, string> names) => new(
        m.Id, m.AuthorType, m.AuthorUserId is { } id && names.TryGetValue(id, out var name) ? name : null,
        m.Body, m.IsInternal, m.CreatedAt);

    private async Task<StaffMessageResponse> ToStaffAsync(Message m, CancellationToken ct) =>
        ToStaff(m, await AuthorNamesAsync([m], ct));
}

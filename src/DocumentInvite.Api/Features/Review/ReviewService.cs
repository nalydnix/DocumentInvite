using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Review;

public sealed record ReviewResult(ReviewResponse? Value, bool NotFound, string? Conflict, bool DeliveryFailed)
{
    public static ReviewResult Ok(ReviewResponse value) => new(value, false, null, false);
    public static ReviewResult Missing() => new(null, true, null, false);
    public static ReviewResult Refused(string reason) => new(null, false, reason, false);
    public static ReviewResult EmailFailed() => new(null, false, null, true);
}

public sealed class ReviewService(
    AppDbContext db,
    IAuditLog audit,
    IEmailSender email,
    IPortalLinkBuilder links,
    IRequestContext context,
    TimeProvider clock)
{
    private const string Stale = "The item was changed by someone else. Reload it and try again.";

    public async Task<ReviewResult> ApproveAsync(Guid itemId, CancellationToken ct)
    {
        var (request, invite, item) = await LoadAsync(itemId, ct);
        if (item is null)
        {
            return ReviewResult.Missing();
        }

        if (request!.CompletedAt is not null)
        {
            return ReviewResult.Refused(RequestLock.Message);
        }

        if (item.Status != RequestItemStatus.Submitted)
        {
            return ReviewResult.Refused("Only a submitted item can be approved.");
        }

        var now = clock.GetUtcNow();
        item.Status = RequestItemStatus.Approved;
        item.RejectionReason = null;
        Stamp(item, now);
        StatusRecalculator.Apply(request!);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync("item.approved", new AuditTarget("item", item.Id), Details(request!, invite!), ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ReviewResult.Refused(Stale);
        }

        return ReviewResult.Ok(ToResponse(request!, invite!, item));
    }

    /// <summary>
    /// Reopens the item with a reason, adds a system message to the party's thread and emails the party.
    /// Only a hash of the portal token is stored, so the email carries a freshly issued link and the
    /// previous one stops working. If the email cannot be delivered everything is rolled back.
    /// </summary>
    public async Task<ReviewResult> RejectAsync(Guid itemId, string reason, CancellationToken ct)
    {
        var (request, invite, item) = await LoadAsync(itemId, ct);
        if (item is null)
        {
            return ReviewResult.Missing();
        }

        if (request!.CompletedAt is not null)
        {
            return ReviewResult.Refused(RequestLock.Message);
        }

        if (item.Status is not (RequestItemStatus.Submitted or RequestItemStatus.Approved))
        {
            return ReviewResult.Refused("Only a submitted or approved item can be rejected.");
        }

        if (invite!.RevokedAt is not null)
        {
            return ReviewResult.Refused("The party's link is revoked. Reissue the link before rejecting an item.");
        }

        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == invite.ContactId, ct);
        if (contact.IsPseudonymised)
        {
            return ReviewResult.Refused("The party's data has been erased, so they cannot be asked to resubmit.");
        }

        reason = reason.Trim();
        var now = clock.GetUtcNow();
        item.Status = RequestItemStatus.Rejected;
        item.RejectionReason = reason;
        Stamp(item, now);
        StatusRecalculator.Apply(request!);

        var token = PortalTokens.Generate();
        invite.TokenHash = PortalTokens.Hash(token);
        // An expired link would make the emailed link useless, so extend it as a reissue does.
        var expiry = InviteService.DefaultExpiry(invite.DueDate);
        var newExpiry = expiry > now ? expiry : now.AddDays(InviteService.LinkValidDaysAfterDueDate);
        if (invite.TokenExpiresAt < newExpiry)
        {
            invite.TokenExpiresAt = newExpiry;
        }

        db.Messages.Add(new Message
        {
            InviteId = invite.Id,
            AuthorType = MessageAuthorType.System,
            Body = $"\"{item.Name}\" was rejected: {reason}",
            IsInternal = false,
            CreatedAt = now,
        });

        var content = EmailTemplates.ItemRejected(new ItemRejectedEmailModel(
            contact.FirstName, request!.Name, item.Name, reason, links.For(token)));

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync("item.rejected", new AuditTarget("item", item.Id), Details(request, invite), ct);
            var sent = await email.SendAsync(EmailMessage.From(contact.Email, content), ct);
            if (!sent.Delivered)
            {
                await transaction.RollbackAsync(ct);
                return ReviewResult.EmailFailed();
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ReviewResult.Refused(Stale);
        }

        return ReviewResult.Ok(ToResponse(request, invite, item));
    }

    private async Task<(Request? Request, Invite? Invite, RequestItem? Item)> LoadAsync(Guid itemId, CancellationToken ct)
    {
        var request = await db.Requests
            .Include(l => l.Invites).ThenInclude(i => i.Items)
            .AsSplitQuery()
            .SingleOrDefaultAsync(l => l.Invites.Any(i => i.Items.Any(x => x.Id == itemId)), ct);
        var invite = request?.Invites.Single(i => i.Items.Any(x => x.Id == itemId));
        return (request, invite, invite?.Items.Single(x => x.Id == itemId));
    }

    private void Stamp(RequestItem item, DateTimeOffset now)
    {
        item.ReviewedBy = context.ActorId;
        item.ReviewedAt = now;
    }

    private static AuditDetails Details(Request request, Invite invite) =>
        AuditDetails.Empty.WithId("requestId", request.Id).WithId("inviteId", invite.Id);

    private static ReviewResponse ToResponse(Request request, Invite invite, RequestItem item) => new(
        item.Id, item.Status, item.RejectionReason, item.ReviewedAt!.Value, invite.Id, invite.Status, request.Id, request.Status);
}

using System.Globalization;
using System.Text.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Reminders;

public enum ReminderTrigger
{
    Scheduled,
    Manual,
}

/// <summary>The counts a run produced. Never holds names, addresses or links.</summary>
/// <param name="InvitesDue">Invites that were due an email in this run.</param>
/// <param name="RemindersSent">Reminder emails sent.</param>
/// <param name="OverdueNoticesSent">Overdue notices sent.</param>
/// <param name="Failed">Invites whose email could not be delivered or saved. They are tried again in the next window.</param>
public sealed record ReminderRunCounts(int InvitesDue, int RemindersSent, int OverdueNoticesSent, int Failed);

/// <param name="AlreadyRan">True when the window had already been run: nothing was sent and the earlier counts are returned.</param>
public sealed record ReminderRunResponse(
    string WindowKey, bool AlreadyRan, int InvitesDue, int RemindersSent, int OverdueNoticesSent, int Failed);

/// <summary>
/// Sends reminders and overdue notices. Idempotent at two levels: a <see cref="JobRun"/> row with a
/// unique (job, window) pair stops a window from running twice, and the per-invite
/// <c>LastReminderAt</c> / <c>OverdueNoticeSentAt</c> timestamps, written in the same transaction
/// as the outbox row, stop an invite from being emailed twice even if two windows overlap.
/// </summary>
public sealed class ReminderJobService(
    AppDbContext db,
    IAuditLog audit,
    IEmailSender email,
    IPortalLinkBuilder links,
    TimeProvider clock)
{
    public const string JobName = "reminders";

    /// <summary>A run that started but never finished (for example after a crash) can be resumed after this long.</summary>
    private static readonly TimeSpan StaleRunAfter = TimeSpan.FromMinutes(15);

    public async Task<ReminderRunResponse> RunAsync(ReminderTrigger trigger, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var windowKey = trigger == ReminderTrigger.Scheduled
            ? now.UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture)
            : $"manual-{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";

        var run = new JobRun { JobName = JobName, WindowKey = windowKey, StartedAt = now };
        db.JobRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var existing = await db.JobRuns.SingleAsync(r => r.JobName == JobName && r.WindowKey == windowKey, ct);
            if (existing.FinishedAt is not null || now - existing.StartedAt < StaleRunAfter)
            {
                return ToResponse(windowKey, alreadyRan: true, Parse(existing.Result));
            }

            // An unfinished run that has gone quiet: resume it. Invites already emailed carry their
            // timestamps, so nothing is sent twice.
            existing.StartedAt = now;
            await db.SaveChangesAsync(ct);
            run = existing;
        }

        var counts = await ProcessAsync(now, ct);

        db.ChangeTracker.Clear();
        db.JobRuns.Attach(run);
        run.FinishedAt = clock.GetUtcNow();
        run.Result = JsonSerializer.Serialize(counts);
        db.Entry(run).Property(r => r.FinishedAt).IsModified = true;
        db.Entry(run).Property(r => r.Result).IsModified = true;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await audit.RecordAsync(
            "job.reminders_run", new AuditTarget("job"),
            AuditDetails.Empty
                .WithEnum("trigger", trigger)
                .WithCount("invitesDue", counts.InvitesDue)
                .WithCount("remindersSent", counts.RemindersSent)
                .WithCount("overdueNoticesSent", counts.OverdueNoticesSent)
                .WithCount("failed", counts.Failed), ct);
        await transaction.CommitAsync(ct);

        return ToResponse(windowKey, alreadyRan: false, counts);
    }

    private async Task<ReminderRunCounts> ProcessAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Active = not revoked, link still valid, not completed, and the party can still be emailed.
        var candidates = await db.Invites.AsNoTracking()
            .Where(i => i.RevokedAt == null
                && i.TokenExpiresAt > now
                && i.Status != CollectionStatus.Completed
                && i.Items.Any(x => x.Status == RequestItemStatus.Outstanding || x.Status == RequestItemStatus.Rejected)
                && db.Contacts.Any(c => c.Id == i.ContactId && !c.IsPseudonymised && c.ErasedAt == null))
            .OrderBy(i => i.SentAt).ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                i.DueDate,
                i.ReminderIntervalDays,
                i.SentAt,
                i.LastReminderAt,
                i.OverdueNoticeSentAt,
            })
            .ToListAsync(ct);

        int due = 0, reminders = 0, overdue = 0, failed = 0;
        foreach (var c in candidates)
        {
            var facts = new ReminderFacts(c.DueDate, c.ReminderIntervalDays, c.SentAt, c.LastReminderAt, c.OverdueNoticeSentAt);
            var action = ReminderEligibility.Decide(facts, now);
            if (action == ReminderAction.None)
            {
                continue;
            }

            due++;
            db.ChangeTracker.Clear();
            var sent = await TrySendAsync(c.Id, action, now, ct);
            if (sent is null)
            {
                // Changed or removed since it was listed: nothing to send.
                due--;
            }
            else if (sent == false)
            {
                failed++;
            }
            else if (action == ReminderAction.Reminder)
            {
                reminders++;
            }
            else
            {
                overdue++;
            }
        }

        db.ChangeTracker.Clear();
        return new ReminderRunCounts(due, reminders, overdue, failed);
    }

    /// <returns>True when sent, false when delivery or saving failed, null when the invite no longer needs an email.</returns>
    private async Task<bool?> TrySendAsync(Guid inviteId, ReminderAction action, DateTimeOffset now, CancellationToken ct)
    {
        var invite = await db.Invites.Include(i => i.Items).SingleOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null || invite.RevokedAt is not null)
        {
            return null;
        }

        var facts = new ReminderFacts(
            invite.DueDate, invite.ReminderIntervalDays, invite.SentAt, invite.LastReminderAt, invite.OverdueNoticeSentAt);
        var outstanding = invite.Items.Count(x => x.Status is RequestItemStatus.Outstanding or RequestItemStatus.Rejected);
        if (outstanding == 0 || ReminderEligibility.Decide(facts, now) != action)
        {
            return null;
        }

        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == invite.ContactId, ct);
        var requestName = await db.Requests.AsNoTracking().Where(l => l.Id == invite.RequestId).Select(l => l.Name).SingleAsync(ct);

        // Only the hash of a portal token is stored, so the previous link cannot be repeated in an
        // email. Each reminder therefore carries a freshly issued link, and the previous one stops
        // working. Expiry, password and the rest of the invite are unchanged.
        var token = PortalTokens.Generate();
        invite.TokenHash = PortalTokens.Hash(token);
        invite.LastReminderAt = now;
        if (action == ReminderAction.OverdueNotice)
        {
            invite.OverdueNoticeSentAt = now;
        }

        var link = links.For(token);
        var content = action == ReminderAction.OverdueNotice
            ? EmailTemplates.Overdue(new OverdueEmailModel(contact.FirstName, requestName, link, invite.DueDate, outstanding))
            : EmailTemplates.Reminder(new ReminderEmailModel(contact.FirstName, requestName, link, invite.DueDate, outstanding));

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Saved before the email goes out, so a concurrent run that already handled this invite
            // fails here on the row version, before anything is delivered.
            await db.SaveChangesAsync(ct);
            var result = await email.SendAsync(EmailMessage.From(contact.Email, content), ct);
            if (!result.Delivered)
            {
                // Roll back so the old link keeps working and the invite is retried next window.
                await transaction.RollbackAsync(ct);
                return false;
            }

            await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static ReminderRunCounts Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReminderRunCounts>(json) ?? new ReminderRunCounts(0, 0, 0, 0);
        }
        catch (JsonException)
        {
            return new ReminderRunCounts(0, 0, 0, 0);
        }
    }

    private static ReminderRunResponse ToResponse(string windowKey, bool alreadyRan, ReminderRunCounts c) =>
        new(windowKey, alreadyRan, c.InvitesDue, c.RemindersSent, c.OverdueNoticesSent, c.Failed);
}

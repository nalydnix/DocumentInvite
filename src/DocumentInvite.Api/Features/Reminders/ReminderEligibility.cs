namespace DocumentInvite.Api.Features.Reminders;

public enum ReminderAction
{
    None,
    Reminder,
    OverdueNotice,
}

/// <summary>What eligibility needs to know about one active invite that still has outstanding items.</summary>
public readonly record struct ReminderFacts(
    DateOnly DueDate,
    int ReminderIntervalDays,
    DateTimeOffset SentAt,
    DateTimeOffset? LastReminderAt,
    DateTimeOffset? OverdueNoticeSentAt);

/// <summary>The single place that decides which email, if any, an invite is due. Pure: no clock, no database.</summary>
public static class ReminderEligibility
{
    /// <summary>
    /// The overdue notice goes out once, as soon as the due date (UTC) has passed. Otherwise a reminder
    /// is due when the interval has passed since the last reminder, or since the invite was sent
    /// when none was sent yet. Sending either email stamps <c>LastReminderAt</c>, so one run never
    /// sends an invite two emails.
    /// </summary>
    public static ReminderAction Decide(ReminderFacts facts, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today > facts.DueDate && facts.OverdueNoticeSentAt is null)
        {
            return ReminderAction.OverdueNotice;
        }

        var last = facts.LastReminderAt ?? facts.SentAt;
        var interval = TimeSpan.FromDays(Math.Max(1, facts.ReminderIntervalDays));
        return now - last >= interval ? ReminderAction.Reminder : ReminderAction.None;
    }
}

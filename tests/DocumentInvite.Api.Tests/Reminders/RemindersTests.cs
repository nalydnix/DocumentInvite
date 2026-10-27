using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Contacts;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Loans;
using DocumentInvite.Api.Features.Reminders;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Reminders;

public class ReminderEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Due = new(2026, 3, 20);

    private static ReminderFacts Facts(
        int interval = 3, DateTimeOffset? sent = null, DateTimeOffset? last = null, DateTimeOffset? overdue = null,
        DateOnly? due = null) =>
        new(due ?? Due, interval, sent ?? Now.AddDays(-1), last, overdue);

    [Fact]
    public void Nothing_is_due_before_the_interval_has_passed_since_sending()
    {
        ReminderEligibility.Decide(Facts(sent: Now.AddDays(-2)), Now).Should().Be(ReminderAction.None);
    }

    [Fact]
    public void A_reminder_is_due_exactly_when_the_interval_has_passed_since_sending()
    {
        ReminderEligibility.Decide(Facts(sent: Now.AddDays(-3)), Now).Should().Be(ReminderAction.Reminder);
    }

    [Fact]
    public void The_last_reminder_takes_over_from_the_send_time()
    {
        var facts = Facts(sent: Now.AddDays(-30), last: Now.AddDays(-2));
        ReminderEligibility.Decide(facts, Now).Should().Be(ReminderAction.None);
        ReminderEligibility.Decide(facts, Now.AddDays(1)).Should().Be(ReminderAction.Reminder);
    }

    [Fact]
    public void The_overdue_notice_is_due_the_day_after_the_due_date_and_only_once()
    {
        var onDueDate = new DateTimeOffset(2026, 3, 20, 23, 0, 0, TimeSpan.Zero);
        var dayAfter = new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.Zero);
        var facts = Facts(interval: 90, sent: onDueDate.AddDays(-1));

        ReminderEligibility.Decide(facts, onDueDate).Should().Be(ReminderAction.None);
        ReminderEligibility.Decide(facts, dayAfter).Should().Be(ReminderAction.OverdueNotice);
        ReminderEligibility.Decide(Facts(interval: 90, sent: onDueDate.AddDays(-1), overdue: dayAfter), dayAfter.AddHours(1))
            .Should().Be(ReminderAction.None);
    }

    [Fact]
    public void Reminders_continue_by_interval_after_the_overdue_notice()
    {
        var facts = Facts(sent: Now.AddDays(-30), last: Now.AddDays(-3), overdue: Now.AddDays(-5), due: new DateOnly(2026, 3, 1));
        ReminderEligibility.Decide(facts, Now).Should().Be(ReminderAction.Reminder);
    }

    [Fact]
    public void An_interval_below_one_day_is_treated_as_one_day()
    {
        ReminderEligibility.Decide(Facts(interval: 0, sent: Now.AddHours(-23)), Now).Should().Be(ReminderAction.None);
        ReminderEligibility.Decide(Facts(interval: 0, sent: Now.AddDays(-1)), Now).Should().Be(ReminderAction.Reminder);
    }
}

public class JobApiKeyTests
{
    [Theory]
    [InlineData("secret", "secret", true)]
    [InlineData("secret", "Secret", false)]
    [InlineData("secret", "secre", false)]
    [InlineData("secret", "", false)]
    [InlineData("secret", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, "anything", false)]
    public void Only_the_exact_configured_key_is_accepted(string? configured, string? supplied, bool expected)
    {
        JobApiKeyAttribute.IsValid(configured, supplied).Should().Be(expected);
    }
}

public partial class RemindersTests : StaffTestBase
{
    private const string ApiKey = "test-jobs-api-key";

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TestClock _clock = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<TimeProvider>(_clock);
        services.Configure<JobsOptions>(o => o.ApiKey = ApiKey);
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.UtcDateTime);

    private HttpClient KeyClient(string? key = ApiKey)
    {
        var client = NewClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    private async Task<ReminderRunResponse> RunScheduledAsync()
    {
        var response = await KeyClient().PostAsync("/internal/jobs/reminders", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ReminderRunResponse>())!;
    }

    private async Task<(HttpClient Officer, Guid InviteId, Guid LoanId)> SendInviteAsync(
        string reference = "LN-2001", string email = "ann@example.com", int interval = 3, int dueInDays = 10)
    {
        var officer = await ClientForAsync($"lo-{reference}@example.com", [StaffRoles.LoanOfficer]);
        var contact = await (await officer.PostAsJsonAsync("/api/contacts",
            new ContactRequest("Ann", "Lee", email, null, PartyType.Borrower))).Content.ReadFromJsonAsync<ContactResponse>();
        var loan = await (await officer.PostAsJsonAsync("/api/loans",
            new LoanRequest(reference, "Acme acquisition", 1_000_000m, "SBA 7(a)"))).Content.ReadFromJsonAsync<LoanResponse>();
        var items = new[]
        {
            new InviteItemRequest("Tax return", ItemKind.FileUpload, "", false, null, true),
            new InviteItemRequest("Bank statement", ItemKind.FileUpload, "", false, null, true),
        };
        var response = await officer.PostAsJsonAsync($"/api/loans/{loan!.Id}/invites",
            new SendInvitesRequest([new SendPartyRequest(contact!.Id, Today.AddDays(dueInDays), interval, items)]));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var invites = await response.Content.ReadFromJsonAsync<List<InviteResponse>>();
        return (officer, invites![0].Id, loan.Id);
    }

    private async Task<List<OutboxEmail>> EmailsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxEmails.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
    }

    private async Task<Invite> InviteRowAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Invites.AsNoTracking().Include(i => i.Items).SingleAsync(i => i.Id == id);
    }

    private async Task<TokenLookupResult> ResolveAsync(string token)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InviteTokenService>().ResolveAsync(token);
    }

    private async Task SetItemStatusesAsync(Guid inviteId, RequestItemStatus status)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.RequestItems.Where(i => i.InviteId == inviteId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, status));
    }

    private static string TokenIn(OutboxEmail email) => LinkPattern().Match(email.TextBody).Groups[1].Value;

    [GeneratedRegex(@"/p/([A-Za-z0-9_-]+)")]
    private static partial Regex LinkPattern();

    // ---- protection of the endpoint ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData("test-jobs-api-ke")]
    public async Task The_cron_endpoint_returns_401_for_a_missing_or_wrong_key(string? key)
    {
        await SendInviteAsync();
        _clock.Now = _clock.Now.AddDays(5);

        var response = await KeyClient(key).PostAsync("/internal/jobs/reminders", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await EmailsAsync()).Should().HaveCount(1, "only the original invite email exists");
    }

    [Fact]
    public async Task A_bearer_token_does_not_replace_the_key_on_the_cron_endpoint()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        (await admin.PostAsync("/internal/jobs/reminders", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Run_now_is_for_admins_only()
    {
        (await NewClient().PostAsync("/api/jobs/reminders/run", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var role in new[] { StaffRoles.LoanOfficer, StaffRoles.Viewer, StaffRoles.Auditor })
        {
            var client = await ClientForAsync($"{role}@example.com", [role]);
            (await client.PostAsync("/api/jobs/reminders/run", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    // ---- sending ----

    [Fact]
    public async Task Nothing_is_sent_before_the_interval_has_passed()
    {
        await SendInviteAsync(interval: 3);
        _clock.Now = _clock.Now.AddDays(2);

        var result = await RunScheduledAsync();

        result.Should().BeEquivalentTo(new { AlreadyRan = false, InvitesDue = 0, RemindersSent = 0, OverdueNoticesSent = 0, Failed = 0 });
        (await EmailsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_reminder_is_sent_once_the_interval_has_passed_and_carries_a_working_link()
    {
        var (_, inviteId, _) = await SendInviteAsync(interval: 3);
        var originalToken = TokenIn((await EmailsAsync()).Single());
        _clock.Now = _clock.Now.AddDays(3);

        var result = await RunScheduledAsync();

        result.RemindersSent.Should().Be(1);
        result.InvitesDue.Should().Be(1);
        var emails = await EmailsAsync();
        emails.Should().HaveCount(2);
        emails[1].Subject.Should().StartWith("Reminder: 2 items still needed");
        emails[1].To.Should().Be("ann@example.com");

        var newToken = TokenIn(emails[1]);
        newToken.Should().NotBe(originalToken);
        (await ResolveAsync(newToken)).IsValid.Should().BeTrue("the reminder link must work");
        (await ResolveAsync(originalToken)).IsValid.Should().BeFalse("the previous link is replaced");
        (await InviteRowAsync(inviteId)).LastReminderAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task Calling_the_job_twice_in_the_same_window_sends_nothing_the_second_time()
    {
        await SendInviteAsync(interval: 3);
        _clock.Now = _clock.Now.AddDays(3);

        var first = await RunScheduledAsync();
        var second = await RunScheduledAsync();

        first.RemindersSent.Should().Be(1);
        first.AlreadyRan.Should().BeFalse();
        second.AlreadyRan.Should().BeTrue();
        second.WindowKey.Should().Be(first.WindowKey);
        second.RemindersSent.Should().Be(1, "the earlier counts are returned");
        (await EmailsAsync()).Should().HaveCount(2, "one invite and one reminder");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.JobRuns.CountAsync(r => r.JobName == "reminders")).Should().Be(1);
    }

    [Fact]
    public async Task A_new_window_does_not_resend_because_the_invite_timestamps_hold()
    {
        await SendInviteAsync(interval: 3);
        _clock.Now = _clock.Now.AddDays(3);
        await RunScheduledAsync();

        _clock.Now = _clock.Now.AddHours(1);
        var next = await RunScheduledAsync();

        next.AlreadyRan.Should().BeFalse();
        next.InvitesDue.Should().Be(0);
        (await EmailsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Reminders_repeat_every_interval()
    {
        await SendInviteAsync(interval: 3, dueInDays: 30);
        for (var round = 0; round < 3; round++)
        {
            _clock.Now = _clock.Now.AddDays(3);
            (await RunScheduledAsync()).RemindersSent.Should().Be(1);
        }

        (await EmailsAsync()).Should().HaveCount(4);
    }

    [Fact]
    public async Task A_single_overdue_notice_is_sent_after_the_due_date()
    {
        var (_, inviteId, _) = await SendInviteAsync(interval: 30, dueInDays: 2);
        _clock.Now = _clock.Now.AddDays(3);

        var first = await RunScheduledAsync();
        _clock.Now = _clock.Now.AddHours(2);
        var second = await RunScheduledAsync();

        first.OverdueNoticesSent.Should().Be(1);
        first.RemindersSent.Should().Be(0);
        second.OverdueNoticesSent.Should().Be(0);
        var emails = await EmailsAsync();
        emails.Should().HaveCount(2);
        emails[1].Subject.Should().StartWith("Overdue:");
        (await InviteRowAsync(inviteId)).OverdueNoticeSentAt.Should().NotBeNull();
        (await ResolveAsync(TokenIn(emails[1]))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_revoked_invite_is_not_reminded()
    {
        var (officer, inviteId, _) = await SendInviteAsync(interval: 3);
        (await officer.PostAsync($"/api/invites/{inviteId}/revoke", null)).EnsureSuccessStatusCode();
        _clock.Now = _clock.Now.AddDays(5);

        (await RunScheduledAsync()).InvitesDue.Should().Be(0);
        (await EmailsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_invite_with_nothing_outstanding_is_not_reminded()
    {
        var (_, inviteId, _) = await SendInviteAsync(interval: 3);
        await SetItemStatusesAsync(inviteId, RequestItemStatus.Submitted);
        _clock.Now = _clock.Now.AddDays(5);

        (await RunScheduledAsync()).InvitesDue.Should().Be(0);
        (await EmailsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_rejected_item_counts_as_outstanding()
    {
        var (_, inviteId, _) = await SendInviteAsync(interval: 3);
        await SetItemStatusesAsync(inviteId, RequestItemStatus.Rejected);
        _clock.Now = _clock.Now.AddDays(3);

        (await RunScheduledAsync()).RemindersSent.Should().Be(1);
    }

    [Fact]
    public async Task An_invite_whose_link_has_expired_is_not_reminded()
    {
        await SendInviteAsync(interval: 3, dueInDays: 1);
        _clock.Now = _clock.Now.AddDays(1 + InviteService.LinkValidDaysAfterDueDate + 1);

        (await RunScheduledAsync()).InvitesDue.Should().Be(0);
    }

    [Fact]
    public async Task A_pseudonymised_contact_is_not_emailed()
    {
        var (_, inviteId, _) = await SendInviteAsync(interval: 3);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contactId = (await db.Invites.AsNoTracking().SingleAsync(i => i.Id == inviteId)).ContactId;
            await db.Contacts.Where(c => c.Id == contactId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsPseudonymised, true));
        }

        _clock.Now = _clock.Now.AddDays(5);

        (await RunScheduledAsync()).InvitesDue.Should().Be(0);
        (await EmailsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Each_invite_is_handled_independently()
    {
        await SendInviteAsync("LN-3001", "a@example.com", interval: 3);
        await SendInviteAsync("LN-3002", "b@example.com", interval: 10);
        _clock.Now = _clock.Now.AddDays(3);

        var result = await RunScheduledAsync();

        result.RemindersSent.Should().Be(1);
        (await EmailsAsync()).Where(e => e.Subject.StartsWith("Reminder")).Should().ContainSingle()
            .Which.To.Should().Be("a@example.com");
    }

    // ---- audit ----

    [Fact]
    public async Task Each_run_is_audited_with_counts_only_and_attributed_to_the_system()
    {
        await SendInviteAsync(interval: 3);
        _clock.Now = _clock.Now.AddDays(3);
        await RunScheduledAsync();
        await RunScheduledAsync();

        var events = (await AuditEventsAsync()).Where(e => e.Action == "job.reminders_run").ToList();

        events.Should().ContainSingle("a repeated call in the same window is not a run");
        var run = events[0];
        run.ActorType.Should().Be(ActorType.System);
        run.ActorId.Should().BeNull();
        run.TargetType.Should().Be("job");
        using var details = JsonDocument.Parse(run.DetailsJson);
        details.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("trigger", "invitesDue", "remindersSent", "overdueNoticesSent", "failed");
        details.RootElement.GetProperty("remindersSent").GetInt32().Should().Be(1);
        details.RootElement.GetProperty("trigger").GetString().Should().Be("Scheduled");
        run.DetailsJson.Should().NotContain("ann@example.com").And.NotContain("Ann");
    }

    // ---- Run now ----

    [Fact]
    public async Task Run_now_uses_the_same_service_and_returns_counts()
    {
        await SendInviteAsync(interval: 3);
        _clock.Now = _clock.Now.AddDays(3);
        var admin = await ClientForAsync("root@example.com", [StaffRoles.Admin]);

        var first = await (await admin.PostAsync("/api/jobs/reminders/run", null)).Content.ReadFromJsonAsync<ReminderRunResponse>();
        var second = await (await admin.PostAsync("/api/jobs/reminders/run", null)).Content.ReadFromJsonAsync<ReminderRunResponse>();

        first!.RemindersSent.Should().Be(1);
        first.WindowKey.Should().StartWith("manual-");
        second!.RemindersSent.Should().Be(0, "the invite timestamps stop a manual run from sending twice");
        (await EmailsAsync()).Should().HaveCount(2);

        var runs = (await AuditEventsAsync()).Where(e => e.Action == "job.reminders_run").ToList();
        runs.Should().HaveCount(2);
        runs.Should().OnlyContain(e => e.ActorType == ActorType.User && e.ActorId != null);
    }

    [Fact]
    public async Task Run_now_does_not_block_the_scheduled_window()
    {
        await SendInviteAsync("LN-4001", "a@example.com", interval: 3);
        var admin = await ClientForAsync("root@example.com", [StaffRoles.Admin]);
        await admin.PostAsync("/api/jobs/reminders/run", null);
        await SendInviteAsync("LN-4002", "b@example.com", interval: 1);
        _clock.Now = _clock.Now.AddDays(1);

        (await RunScheduledAsync()).AlreadyRan.Should().BeFalse();
    }
}

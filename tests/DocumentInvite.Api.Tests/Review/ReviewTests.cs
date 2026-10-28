using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Loans;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Features.Review;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Files;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentInvite.Api.Tests.Review;

public class ReviewTests : StaffTestBase
{
    /// <summary>Stores the email as the demo sender does, but reports a failed delivery while <see cref="Fail"/> is set.</summary>
    private sealed class SwitchableEmailSender(DemoOutboxEmailSender inner) : IEmailSender
    {
        public static volatile bool Fail;

        public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var result = await inner.SendAsync(message, cancellationToken);
            return Fail ? result with { Delivered = false, Error = "boom" } : result;
        }
    }

    private readonly FakeBlobStore _blobs = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(_blobs);
        services.RemoveAll<IEmailSender>();
        services.AddScoped<IEmailSender>(sp => new SwitchableEmailSender(sp.GetRequiredService<DemoOutboxEmailSender>()));
    }

    public override async Task DisposeAsync()
    {
        SwitchableEmailSender.Fail = false;
        await base.DisposeAsync();
    }

    private sealed record Seeded(Guid LoanId, Guid InviteId, Guid FileItemId, Guid FieldItemId, Guid FileId, byte[] TokenHash);

    private async Task<Seeded> SeedAsync(
        RequestItemStatus fileStatus = RequestItemStatus.Submitted, string reference = "LN-7",
        DateOnly? due = null, bool revoked = false, DateTimeOffset? tokenExpiresAt = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contact = new Contact { FirstName = "Pat", LastName = "Doe", Email = $"{Guid.NewGuid():N}@example.com" };
        var loan = new Loan { Reference = reference, Name = "Acme loan", LoanType = "SBA", Amount = 5, CreatedAt = DateTimeOffset.UtcNow };
        var fileItem = new RequestItem
        {
            Name = "Bank statements", Kind = ItemKind.FileUpload, Instructions = "x", IsRequired = true, Status = fileStatus,
        };
        var fieldItem = new RequestItem
        {
            Name = "Years", Kind = ItemKind.FormField, FieldType = FieldType.Number, Instructions = "x",
            IsRequired = true, Status = RequestItemStatus.Submitted, FieldValue = "7",
        };
        var hash = RandomNumberGenerator.GetBytes(32);
        var invite = new Invite
        {
            LoanId = loan.Id,
            ContactId = contact.Id,
            TokenHash = hash,
            TokenExpiresAt = tokenExpiresAt ?? DateTimeOffset.UtcNow.AddDays(30),
            DueDate = due ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            SentAt = DateTimeOffset.UtcNow,
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null,
            Items = [fileItem, fieldItem],
        };
        loan.Invites.Add(invite);
        var file = new StoredFile
        {
            RequestItemId = fileItem.Id,
            BlobName = $"invites/{invite.Id}/{Guid.NewGuid()}",
            OriginalFileName = "scan.png",
            StoredFileName = "LN-7_Doe_Bank-statements_1.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            Sequence = 1,
            Status = StoredFileStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Contacts.Add(contact);
        db.Loans.Add(loan);
        db.StoredFiles.Add(file);
        StatusRecalculator.Apply(loan);
        await db.SaveChangesAsync();
        return new Seeded(loan.Id, invite.Id, fileItem.Id, fieldItem.Id, file.Id, hash);
    }

    private async Task<HttpClient> OfficerAsync() => await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ---- approve ----

    [Fact]
    public async Task Approve_marks_the_item_approved_recalculates_statuses_and_audits_ids_only()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();

        var response = await officer.PostAsync($"/api/items/{seed.FileItemId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ReviewResponse>())!;
        body.ItemStatus.Should().Be(RequestItemStatus.Approved);
        body.InviteStatus.Should().Be(CollectionStatus.Submitted);

        var approveField = await officer.PostAsync($"/api/items/{seed.FieldItemId}/approve", null);
        var last = (await approveField.Content.ReadFromJsonAsync<ReviewResponse>())!;
        last.InviteStatus.Should().Be(CollectionStatus.Completed);
        last.LoanStatus.Should().Be(CollectionStatus.ReadyToComplete);
        (await DbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.Id == seed.LoanId))).CompletedAt.Should().BeNull();

        var events = (await AuditEventsAsync()).Where(e => e.Action == "item.approved").ToList();
        events.Should().HaveCount(2);
        events[0].TargetId.Should().Be(seed.FileItemId);
        events[0].DetailsJson.Should().Contain(seed.LoanId.ToString()).And.NotContain("Bank");
    }

    [Fact]
    public async Task Approve_refuses_an_item_that_is_not_submitted_and_unknown_items_are_404()
    {
        var seed = await SeedAsync(RequestItemStatus.Outstanding);
        var officer = await OfficerAsync();

        (await officer.PostAsync($"/api/items/{seed.FileItemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await officer.PostAsync($"/api/items/{Guid.NewGuid()}/approve", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- reject ----

    [Fact]
    public async Task Reject_requires_a_reason()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();

        foreach (var reason in new[] { "", "   ", null })
        {
            var response = await officer.PostAsJsonAsync($"/api/items/{seed.FileItemId}/reject", new RejectItemRequest(reason));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await DbAsync(db => db.RequestItems.AsNoTracking().SingleAsync(i => i.Id == seed.FileItemId)))
            .Status.Should().Be(RequestItemStatus.Submitted);
    }

    [Fact]
    public async Task Reject_reopens_the_item_adds_a_system_message_emails_the_party_and_audits()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();

        var response = await officer.PostAsJsonAsync(
            $"/api/items/{seed.FileItemId}/reject", new RejectItemRequest("Statement is blurry"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ReviewResponse>())!;
        body.ItemStatus.Should().Be(RequestItemStatus.Rejected);
        body.InviteStatus.Should().Be(CollectionStatus.InProgress);

        var item = await DbAsync(db => db.RequestItems.AsNoTracking().SingleAsync(i => i.Id == seed.FileItemId));
        item.RejectionReason.Should().Be("Statement is blurry");
        item.ReviewedAt.Should().NotBeNull();

        var message = await DbAsync(db => db.Messages.AsNoTracking().SingleAsync());
        message.InviteId.Should().Be(seed.InviteId);
        message.AuthorType.Should().Be(MessageAuthorType.System);
        message.IsInternal.Should().BeFalse();
        message.Body.Should().Contain("Statement is blurry");

        var emails = await DbAsync(db => db.OutboxEmails.AsNoTracking().ToListAsync());
        emails.Should().ContainSingle().Which.TextBody.Should().Contain("Statement is blurry").And.Contain("/p/");
        var invite = await DbAsync(db => db.Invites.AsNoTracking().SingleAsync(i => i.Id == seed.InviteId));
        invite.TokenHash.Should().NotEqual(seed.TokenHash, "the emailed link is freshly issued");

        var audit = (await AuditEventsAsync()).Single(e => e.Action == "item.rejected");
        audit.TargetId.Should().Be(seed.FileItemId);
        audit.DetailsJson.Should().NotContain("blurry");
    }

    [Fact]
    public async Task Reject_extends_an_expired_link_so_the_emailed_link_works()
    {
        var seed = await SeedAsync(
            due: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-60)), tokenExpiresAt: DateTimeOffset.UtcNow.AddDays(-5));
        var officer = await OfficerAsync();

        var response = await officer.PostAsJsonAsync($"/api/items/{seed.FileItemId}/reject", new RejectItemRequest("Blurry"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var email = await DbAsync(db => db.OutboxEmails.AsNoTracking().SingleAsync());
        var token = System.Text.RegularExpressions.Regex.Match(email.TextBody, "/p/([A-Za-z0-9_-]+)").Groups[1].Value;
        using var scope = Factory.Services.CreateScope();
        var lookup = await scope.ServiceProvider.GetRequiredService<InviteTokenService>().ResolveAsync(token);
        lookup.Failure.Should().BeNull();
        lookup.Invite.Should().NotBeNull();
    }

    [Fact]
    public async Task Reject_is_rolled_back_when_the_email_cannot_be_delivered()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();
        SwitchableEmailSender.Fail = true;

        var response = await officer.PostAsJsonAsync(
            $"/api/items/{seed.FileItemId}/reject", new RejectItemRequest("Blurry"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await DbAsync(db => db.RequestItems.AsNoTracking().SingleAsync(i => i.Id == seed.FileItemId)))
            .Status.Should().Be(RequestItemStatus.Submitted);
        (await DbAsync(db => db.Messages.CountAsync())).Should().Be(0);
        (await DbAsync(db => db.Invites.AsNoTracking().SingleAsync(i => i.Id == seed.InviteId)))
            .TokenHash.Should().Equal(seed.TokenHash);
        (await AuditEventsAsync()).Should().NotContain(e => e.Action == "item.rejected");
    }

    [Fact]
    public async Task Reject_refuses_a_revoked_invite()
    {
        var seed = await SeedAsync(revoked: true);
        var officer = await OfficerAsync();

        var response = await officer.PostAsJsonAsync($"/api/items/{seed.FileItemId}/reject", new RejectItemRequest("No"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- anonymous ----

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var seed = await SeedAsync();
        var anonymous = NewClient();

        (await anonymous.PostAsync($"/api/items/{seed.FileItemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/files/{seed.FileId}/preview-url")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- preview and download ----

    [Fact]
    public async Task Preview_and_download_return_short_lived_urls_and_are_audited()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();

        var preview = await officer.GetAsync($"/api/files/{seed.FileId}/preview-url");
        var download = await officer.GetAsync($"/api/files/{seed.FileId}/download-url");

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        _blobs.ReadUrls.Should().HaveCount(2);
        _blobs.ReadUrls[0].DownloadName.Should().BeNull();
        _blobs.ReadUrls[1].DownloadName.Should().Be("LN-7_Doe_Bank-statements_1.pdf");
        _blobs.ReadUrls.Should().OnlyContain(r => r.Lifetime <= TimeSpan.FromMinutes(5));

        var actions = (await AuditEventsAsync()).Select(e => e.Action).ToList();
        actions.Should().Contain(["file.previewed", "file.downloaded"]);

        (await officer.GetAsync($"/api/files/{Guid.NewGuid()}/preview-url")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- dashboard and detail ----

    [Fact]
    public async Task Loan_detail_shows_rejection_reason_and_ready_files_for_each_item()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();
        await officer.PostAsJsonAsync($"/api/items/{seed.FileItemId}/reject", new RejectItemRequest("Blurry"));

        var loan = (await officer.GetFromJsonAsync<LoanResponse>($"/api/loans/{seed.LoanId}"))!;

        var fileItem = loan.Invites.Single().Items.Single(i => i.Id == seed.FileItemId);
        fileItem.Status.Should().Be(RequestItemStatus.Rejected);
        fileItem.RejectionReason.Should().Be("Blurry");
        fileItem.Files.Should().ContainSingle().Which.Id.Should().Be(seed.FileId);
        loan.Invites.Single().Items.Single(i => i.Id == seed.FieldItemId).FieldValue.Should().Be("7");
    }

    [Fact]
    public async Task Dashboard_lists_progress_due_date_and_overdue_flag_and_filters()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var overdue = await SeedAsync(RequestItemStatus.Outstanding, "LN-OVER", yesterday);
        var done = await SeedAsync(RequestItemStatus.Approved, "LN-DONE", yesterday);
        await DbAsync(async db =>
        {
            await db.RequestItems.Where(i => i.InviteId == done.InviteId).ExecuteUpdateAsync(
                s => s.SetProperty(i => i.Status, RequestItemStatus.Approved));
            return 0;
        });
        var officer = await ClientForAsync("v@example.com", [StaffRoles.LoanOfficer]);

        var page = (await officer.GetFromJsonAsync<PagedResponse<LoanSummaryResponse>>("/api/loans"))!;

        var over = page.Items.Single(l => l.Reference == "LN-OVER");
        over.IsOverdue.Should().BeTrue();
        over.ItemsTotal.Should().Be(2);
        over.ItemsApproved.Should().Be(0);
        over.PercentComplete.Should().Be(0);
        over.DueDate.Should().Be(yesterday);
        var complete = page.Items.Single(l => l.Reference == "LN-DONE");
        complete.IsOverdue.Should().BeFalse("nothing is left for the party to provide");
        complete.PercentComplete.Should().Be(100);

        var search = (await officer.GetFromJsonAsync<PagedResponse<LoanSummaryResponse>>("/api/loans?search=OVER"))!;
        search.Items.Should().ContainSingle().Which.Id.Should().Be(overdue.LoanId);
    }

    [Fact]
    public async Task Reject_response_is_json_problem_details_on_validation_error()
    {
        var seed = await SeedAsync();
        var officer = await OfficerAsync();

        var response = await officer.PostAsJsonAsync($"/api/items/{seed.FileItemId}/reject", new RejectItemRequest(""));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain("reason");
    }
}

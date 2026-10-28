using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Loans;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Loans;

public class LoanCompletionTests : PortalTestBase
{
    private async Task SetItemsAsync(Guid loanId, RequestItemStatus status)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loan = await db.Loans.Include(l => l.Invites).ThenInclude(i => i.Items).SingleAsync(l => l.Id == loanId);
        foreach (var item in loan.Invites.SelectMany(i => i.Items))
        {
            item.Status = status;
        }

        StatusRecalculator.Apply(loan);
        await db.SaveChangesAsync();
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<(HttpClient Officer, Party Party, Guid LoanId)> ReadyLoanAsync()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        await SetItemsAsync(party.Invite.LoanId, RequestItemStatus.Approved);
        return (officer, party, party.Invite.LoanId);
    }

    private async Task<(HttpClient Officer, Party Party, Guid LoanId)> CompletedLoanAsync()
    {
        var (officer, party, loanId) = await ReadyLoanAsync();
        (await officer.PostAsync($"/api/loans/{loanId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (officer, party, loanId);
    }

    private static async Task AssertLockedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        problem.Detail.Should().Contain("Reopen the loan");
    }

    [Fact]
    public async Task Approving_every_item_makes_the_loan_ReadyToComplete_and_does_not_stamp_CompletedAt()
    {
        var (officer, _, loanId) = await ReadyLoanAsync();

        var loan = (await officer.GetFromJsonAsync<LoanResponse>($"/api/loans/{loanId}"))!;

        loan.Status.Should().Be(CollectionStatus.ReadyToComplete);
        loan.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Complete_is_refused_unless_the_loan_is_ReadyToComplete()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);

        var response = await officer.PostAsync($"/api/loans/{party.Invite.LoanId}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.Id == party.Invite.LoanId))).CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Complete_stamps_CompletedAt_and_audits_loan_completed()
    {
        var (officer, _, loanId) = await ReadyLoanAsync();

        var response = await officer.PostAsync($"/api/loans/{loanId}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var loan = (await response.Content.ReadFromJsonAsync<LoanResponse>())!;
        loan.Status.Should().Be(CollectionStatus.Completed);
        loan.CompletedAt.Should().NotBeNull();
        (await AuditEventsAsync()).Should().ContainSingle(e => e.Action == "loan.completed" && e.TargetId == loanId);

        (await officer.PostAsync($"/api/loans/{loanId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Complete_and_reopen_need_a_staff_role_and_an_existing_loan()
    {
        var (_, _, loanId) = await ReadyLoanAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        var auditor = await ClientForAsync("aud@example.com", [StaffRoles.Auditor]);

        (await auditor.PostAsync($"/api/loans/{loanId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await auditor.PostAsJsonAsync($"/api/loans/{loanId}/reopen", new ReopenLoanRequest("x")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().PostAsync($"/api/loans/{loanId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsync($"/api/loans/{Guid.NewGuid()}/complete", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync($"/api/loans/{loanId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_completed_loan_refuses_review_and_invite_changes()
    {
        var (officer, party, loanId) = await CompletedLoanAsync();

        await AssertLockedAsync(await officer.PostAsync($"/api/items/{party.FileItemId}/approve", null));
        await AssertLockedAsync(await officer.PostAsJsonAsync($"/api/items/{party.FileItemId}/reject", new { reason = "Blurry" }));
        await AssertLockedAsync(await officer.PostAsync($"/api/invites/{party.Invite.Id}/reissue", null));
        await AssertLockedAsync(await officer.PostAsync($"/api/invites/{party.Invite.Id}/revoke", null));

        var loan = await DbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.Id == loanId));
        loan.Status.Should().Be(CollectionStatus.Completed);
        loan.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_completed_loan_refuses_portal_uploads_fields_and_notes()
    {
        var (_, party, _) = await CompletedLoanAsync();
        var portal = await SessionAsync(party.Token);

        var slot = await portal.PostAsJsonAsync($"/api/portal/items/{party.FileItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 });
        var field = await portal.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "5" });
        var note = await portal.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/note", new { note = "hi" });

        foreach (var response in new[] { slot, field, note })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await CodeOfAsync(response)).Should().Be("loan_completed");
        }
    }

    [Fact]
    public async Task Reopen_requires_a_reason_and_a_completed_loan()
    {
        var (officer, _, loanId) = await ReadyLoanAsync();

        (await officer.PostAsJsonAsync($"/api/loans/{loanId}/reopen", new ReopenLoanRequest("Needs a recheck")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        await officer.PostAsync($"/api/loans/{loanId}/complete", null);
        (await officer.PostAsJsonAsync($"/api/loans/{loanId}/reopen", new ReopenLoanRequest(" ")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.Id == loanId))).CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reopen_clears_CompletedAt_posts_a_system_note_on_each_invite_audits_and_unlocks_review()
    {
        var (officer, party, loanId) = await CompletedLoanAsync();

        var response = await officer.PostAsJsonAsync($"/api/loans/{loanId}/reopen", new ReopenLoanRequest("Bank statement was wrong"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var loan = (await response.Content.ReadFromJsonAsync<LoanResponse>())!;
        loan.CompletedAt.Should().BeNull();
        loan.Status.Should().NotBe(CollectionStatus.Completed);

        var messages = await DbAsync(db => db.Messages.AsNoTracking().Where(m => m.InviteId == party.Invite.Id).ToListAsync());
        messages.Should().ContainSingle(m =>
            m.AuthorType == MessageAuthorType.System && m.Body.Contains("Bank statement was wrong") && !m.IsInternal);
        (await AuditEventsAsync()).Should().ContainSingle(e => e.Action == "loan.reopened" && e.TargetId == loanId);

        // Rejecting an approved item works again and moves the loan back to In progress.
        var reject = await officer.PostAsJsonAsync($"/api/items/{party.FileItemId}/reject", new { reason = "Blurry" });
        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await officer.GetFromJsonAsync<LoanResponse>($"/api/loans/{loanId}"))!.Status.Should().Be(CollectionStatus.InProgress);
    }

    [Fact]
    public async Task Rejecting_an_approved_item_on_a_ReadyToComplete_loan_still_works()
    {
        var (officer, party, loanId) = await ReadyLoanAsync();

        var reject = await officer.PostAsJsonAsync($"/api/items/{party.FileItemId}/reject", new { reason = "Blurry" });

        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await officer.GetFromJsonAsync<LoanResponse>($"/api/loans/{loanId}"))!.Status.Should().Be(CollectionStatus.InProgress);
    }

    [Fact]
    public async Task A_reopened_loan_is_ignored_by_the_retention_purge()
    {
        var (officer, _, loanId) = await CompletedLoanAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        await DbAsync(async db =>
        {
            await db.Loans.ExecuteUpdateAsync(s => s.SetProperty(l => l.CompletedAt, DateTimeOffset.UtcNow.AddYears(-5)));
            return 0;
        });
        (await admin.GetStringAsync("/api/gdpr/retention/preview")).Should().Contain(loanId.ToString());

        await officer.PostAsJsonAsync($"/api/loans/{loanId}/reopen", new ReopenLoanRequest("Recheck"));

        (await admin.GetStringAsync("/api/gdpr/retention/preview")).Should().NotContain(loanId.ToString());
    }
}

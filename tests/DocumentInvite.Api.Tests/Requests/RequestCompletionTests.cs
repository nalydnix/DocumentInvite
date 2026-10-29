using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Requests;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Requests;

public class RequestCompletionTests : PortalTestBase
{
    private async Task SetItemsAsync(Guid requestId, RequestItemStatus status)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests.Include(l => l.Invites).ThenInclude(i => i.Items).SingleAsync(l => l.Id == requestId);
        foreach (var item in request.Invites.SelectMany(i => i.Items))
        {
            item.Status = status;
        }

        StatusRecalculator.Apply(request);
        await db.SaveChangesAsync();
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<(HttpClient Requester, Party Party, Guid RequestId)> ReadyRequestAsync()
    {
        var requester = await RequesterAsync();
        var party = await SendInviteAsync(requester);
        await SetItemsAsync(party.Invite.RequestId, RequestItemStatus.Approved);
        return (requester, party, party.Invite.RequestId);
    }

    private async Task<(HttpClient Requester, Party Party, Guid RequestId)> CompletedRequestAsync()
    {
        var (requester, party, requestId) = await ReadyRequestAsync();
        (await requester.PostAsync($"/api/requests/{requestId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (requester, party, requestId);
    }

    private static async Task AssertLockedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        problem.Detail.Should().Contain("Reopen the request");
    }

    [Fact]
    public async Task Approving_every_item_makes_the_request_ReadyToComplete_and_does_not_stamp_CompletedAt()
    {
        var (requester, _, requestId) = await ReadyRequestAsync();

        var request = (await requester.GetFromJsonAsync<RequestResponse>($"/api/requests/{requestId}"))!;

        request.Status.Should().Be(CollectionStatus.ReadyToComplete);
        request.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Complete_is_refused_unless_the_request_is_ReadyToComplete()
    {
        var requester = await RequesterAsync();
        var party = await SendInviteAsync(requester);

        var response = await requester.PostAsync($"/api/requests/{party.Invite.RequestId}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await DbAsync(db => db.Requests.AsNoTracking().SingleAsync(l => l.Id == party.Invite.RequestId))).CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Complete_stamps_CompletedAt_and_audits_request_completed()
    {
        var (requester, _, requestId) = await ReadyRequestAsync();

        var response = await requester.PostAsync($"/api/requests/{requestId}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = (await response.Content.ReadFromJsonAsync<RequestResponse>())!;
        request.Status.Should().Be(CollectionStatus.Completed);
        request.CompletedAt.Should().NotBeNull();
        (await AuditEventsAsync()).Should().ContainSingle(e => e.Action == "request.completed" && e.TargetId == requestId);

        (await requester.PostAsync($"/api/requests/{requestId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Complete_and_reopen_need_a_staff_role_and_an_existing_request()
    {
        var (_, _, requestId) = await ReadyRequestAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        var auditor = await ClientForAsync("aud@example.com", [StaffRoles.Auditor]);

        (await auditor.PostAsync($"/api/requests/{requestId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await auditor.PostAsJsonAsync($"/api/requests/{requestId}/reopen", new ReopenRequestRequest("x")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().PostAsync($"/api/requests/{requestId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsync($"/api/requests/{Guid.NewGuid()}/complete", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync($"/api/requests/{requestId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_completed_request_refuses_review_and_invite_changes()
    {
        var (requester, party, requestId) = await CompletedRequestAsync();

        await AssertLockedAsync(await requester.PostAsync($"/api/items/{party.FileItemId}/approve", null));
        await AssertLockedAsync(await requester.PostAsJsonAsync($"/api/items/{party.FileItemId}/reject", new { reason = "Blurry" }));
        await AssertLockedAsync(await requester.PostAsync($"/api/invites/{party.Invite.Id}/reissue", null));
        await AssertLockedAsync(await requester.PostAsync($"/api/invites/{party.Invite.Id}/revoke", null));

        var request = await DbAsync(db => db.Requests.AsNoTracking().SingleAsync(l => l.Id == requestId));
        request.Status.Should().Be(CollectionStatus.Completed);
        request.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_completed_request_refuses_portal_uploads_fields_and_notes()
    {
        var (_, party, _) = await CompletedRequestAsync();
        var portal = await SessionAsync(party.Token);

        var slot = await portal.PostAsJsonAsync($"/api/portal/items/{party.FileItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 });
        var field = await portal.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "5" });
        var note = await portal.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/note", new { note = "hi" });

        foreach (var response in new[] { slot, field, note })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await CodeOfAsync(response)).Should().Be("request_completed");
        }
    }

    [Fact]
    public async Task Reopen_requires_a_reason_and_a_completed_request()
    {
        var (requester, _, requestId) = await ReadyRequestAsync();

        (await requester.PostAsJsonAsync($"/api/requests/{requestId}/reopen", new ReopenRequestRequest("Needs a recheck")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        await requester.PostAsync($"/api/requests/{requestId}/complete", null);
        (await requester.PostAsJsonAsync($"/api/requests/{requestId}/reopen", new ReopenRequestRequest(" ")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.Requests.AsNoTracking().SingleAsync(l => l.Id == requestId))).CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reopen_clears_CompletedAt_posts_a_system_note_on_each_invite_audits_and_unlocks_review()
    {
        var (requester, party, requestId) = await CompletedRequestAsync();

        var response = await requester.PostAsJsonAsync($"/api/requests/{requestId}/reopen", new ReopenRequestRequest("Bank statement was wrong"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = (await response.Content.ReadFromJsonAsync<RequestResponse>())!;
        request.CompletedAt.Should().BeNull();
        request.Status.Should().NotBe(CollectionStatus.Completed);

        var messages = await DbAsync(db => db.Messages.AsNoTracking().Where(m => m.InviteId == party.Invite.Id).ToListAsync());
        messages.Should().ContainSingle(m =>
            m.AuthorType == MessageAuthorType.System && m.Body.Contains("Bank statement was wrong") && !m.IsInternal);
        (await AuditEventsAsync()).Should().ContainSingle(e => e.Action == "request.reopened" && e.TargetId == requestId);

        // Rejecting an approved item works again and moves the request back to In progress.
        var reject = await requester.PostAsJsonAsync($"/api/items/{party.FileItemId}/reject", new { reason = "Blurry" });
        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await requester.GetFromJsonAsync<RequestResponse>($"/api/requests/{requestId}"))!.Status.Should().Be(CollectionStatus.InProgress);
    }

    [Fact]
    public async Task Rejecting_an_approved_item_on_a_ReadyToComplete_request_still_works()
    {
        var (requester, party, requestId) = await ReadyRequestAsync();

        var reject = await requester.PostAsJsonAsync($"/api/items/{party.FileItemId}/reject", new { reason = "Blurry" });

        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await requester.GetFromJsonAsync<RequestResponse>($"/api/requests/{requestId}"))!.Status.Should().Be(CollectionStatus.InProgress);
    }

    [Fact]
    public async Task A_reopened_request_is_ignored_by_the_retention_purge()
    {
        var (requester, _, requestId) = await CompletedRequestAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        await DbAsync(async db =>
        {
            await db.Requests.ExecuteUpdateAsync(s => s.SetProperty(l => l.CompletedAt, DateTimeOffset.UtcNow.AddYears(-5)));
            return 0;
        });
        (await admin.GetStringAsync("/api/gdpr/retention/preview")).Should().Contain(requestId.ToString());

        await requester.PostAsJsonAsync($"/api/requests/{requestId}/reopen", new ReopenRequestRequest("Recheck"));

        (await admin.GetStringAsync("/api/gdpr/retention/preview")).Should().NotContain(requestId.ToString());
    }
}

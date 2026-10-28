using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DocumentInvite.Api.Infrastructure.Persistence;

namespace DocumentInvite.Api.Tests.Portal;

public class PortalTests : PortalTestBase
{
    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ---- token exchange ----

    [Fact]
    public async Task A_valid_token_gives_a_session_and_the_open_is_audited_with_the_party_as_actor()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);

        var session = await SessionAsync(party.Token, acceptPrivacy: false);
        var invite = await JsonOf(await session.GetAsync("/api/portal/invite"));

        invite.GetProperty("items").GetArrayLength().Should().Be(4);
        invite.GetProperty("privacyAccepted").GetBoolean().Should().BeFalse();
        invite.GetProperty("status").GetString().Should().Be("InProgress");

        var opened = (await AuditEventsAsync()).Single(e => e.Action == "portal.opened");
        opened.ActorType.Should().Be(ActorType.Party);
        opened.ActorId.Should().Be(party.ContactId);
        opened.TargetId.Should().Be(party.Invite.Id);
        (await InviteRowAsync(party.Invite.Id)).PortalOpenedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Unknown_revoked_and_expired_tokens_show_a_friendly_error()
    {
        var officer = await OfficerAsync();
        var revoked = await SendInviteAsync(officer);
        var expired = await SendInviteAsync(officer);
        (await officer.PostAsync($"/api/invites/{revoked.Invite.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Invites.Where(i => i.Id == expired.Invite.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.TokenExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        }

        var unknown = await ExchangeAsync("not-a-real-token");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOfAsync(unknown)).Should().Be("link_not_found");

        var revokedResponse = await ExchangeAsync(revoked.Token);
        revokedResponse.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await CodeOfAsync(revokedResponse)).Should().Be("link_revoked");

        var expiredResponse = await ExchangeAsync(expired.Token);
        expiredResponse.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await CodeOfAsync(expiredResponse)).Should().Be("link_expired");
        (await JsonOf(expiredResponse)).GetProperty("detail").GetString().Should().Contain("expired");
    }

    [Fact]
    public async Task A_portal_password_is_stored_hashed_and_required_for_a_session()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer, password: "correct-horse");

        party.Invite.HasPassword.Should().BeTrue();
        var stored = (await InviteRowAsync(party.Invite.Id)).PasswordHash;
        stored.Should().NotBeNull().And.NotContain("correct-horse");
        using (var scope = Factory.Services.CreateScope())
        {
            var row = await InviteRowAsync(party.Invite.Id);
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<Invite>>()
                .VerifyHashedPassword(row, row.PasswordHash!, "correct-horse").Should().NotBe(PasswordVerificationResult.Failed);
        }

        var missing = await ExchangeAsync(party.Token);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOfAsync(missing)).Should().Be("password_required");

        var wrong = await ExchangeAsync(party.Token, "wrong-password");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOfAsync(wrong)).Should().Be("invalid_password");
        (await AuditEventsAsync()).Should().NotContain(e => e.Action == "portal.opened");

        (await ExchangeAsync(party.Token, "correct-horse")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_portal_password_that_is_too_short_is_refused_when_sending()
    {
        var officer = await OfficerAsync();
        var contact = await officer.PostAsJsonAsync("/api/contacts",
            new DocumentInvite.Api.Features.Contacts.ContactRequest("Ann", "Lee", "short@example.com", null, PartyType.Borrower));
        var contactId = (await contact.Content.ReadFromJsonAsync<DocumentInvite.Api.Features.Contacts.ContactResponse>())!.Id;
        var loan = await officer.PostAsJsonAsync("/api/loans", new DocumentInvite.Api.Features.Loans.LoanRequest("LN-9", "Loan", 1m, "SBA"));
        var loanId = (await loan.Content.ReadFromJsonAsync<DocumentInvite.Api.Features.Loans.LoanResponse>())!.Id;

        var response = await officer.PostAsJsonAsync($"/api/loans/{loanId}/invites", new SendInvitesRequest([new SendPartyRequest(
            contactId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), null,
            [new InviteItemRequest("Tax", ItemKind.FileUpload, "", false, null, true)], "short")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Portal_responses_do_not_leak_the_token_through_referrer_or_caches()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);

        var response = await ExchangeAsync(party.Token);
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await ExchangeAsync("nope")).Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
    }

    // ---- scheme separation ----

    [Fact]
    public async Task Portal_endpoints_accept_only_a_portal_session()
    {
        var officer = await OfficerAsync();
        await SendInviteAsync(officer);

        (await officer.GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await NewClient().GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var forged = NewClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        (await forged.GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_portal_session_cannot_call_staff_endpoints()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        (await session.GetAsync("/api/loans")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await session.GetAsync($"/api/invites/{party.Invite.Id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await session.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_ends_when_the_invite_is_revoked_or_the_link_is_reissued()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);
        (await session.GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await officer.PostAsync($"/api/invites/{party.Invite.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await officer.PostAsync($"/api/invites/{party.Invite.Id}/reissue", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var fresh = await SessionAsync(await TokenForAsync(party.ContactId));
        (await fresh.GetAsync("/api/portal/invite")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- privacy notice ----

    [Fact]
    public async Task Nothing_can_be_uploaded_or_saved_before_the_privacy_notice_is_accepted()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token, acceptPrivacy: false);

        var slot = await session.PostAsJsonAsync($"/api/portal/items/{party.FileItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 });
        slot.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOfAsync(slot)).Should().Be("privacy_required");
        (await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "3" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/note", new { note = "hi" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FilesAsync(party.FileItemId)).Should().BeEmpty();

        (await session.PostAsync("/api/portal/privacy", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var accepted = (await InviteRowAsync(party.Invite.Id)).PrivacyAcceptedAt;
        accepted.Should().NotBeNull();
        (await session.PostAsync("/api/portal/privacy", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await InviteRowAsync(party.Invite.Id)).PrivacyAcceptedAt.Should().Be(accepted);

        var events = (await AuditEventsAsync()).Where(e => e.Action == "portal.privacy_accepted").ToList();
        events.Should().ContainSingle().Which.ActorType.Should().Be(ActorType.Party);
        (await session.PostAsJsonAsync($"/api/portal/items/{party.FileItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- uploads ----

    [Fact]
    public async Task An_upload_goes_through_a_slot_and_completion_and_is_audited_with_the_party_as_actor()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        var response = await UploadAsync(session, party.FileItemId, "My Tax Return.pdf");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await JsonOf(response)).GetProperty("fileName").GetString().Should().Be("My Tax Return.pdf");

        var file = (await FilesAsync(party.FileItemId)).Single();
        file.Status.Should().Be(StoredFileStatus.Ready);
        file.StoredFileName.Should().Be($"LN-0001_Lee1_Tax-return_1.pdf");

        var row = await InviteRowAsync(party.Invite.Id);
        row.Items.Single(i => i.Id == party.FileItemId).Status.Should().Be(RequestItemStatus.Submitted);

        var uploaded = (await AuditEventsAsync()).Single(e => e.Action == "file.uploaded");
        uploaded.ActorType.Should().Be(ActorType.Party);
        uploaded.ActorId.Should().Be(party.ContactId);

        var view = await JsonOf(await session.GetAsync("/api/portal/invite"));
        view.GetProperty("completedItemCount").GetInt32().Should().Be(1);
        var item = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == party.FileItemId);
        item.GetProperty("files").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("fileName").GetString().Should().Be("My Tax Return.pdf");
    }

    [Fact]
    public async Task A_single_file_item_keeps_only_the_latest_file_and_a_multi_file_item_keeps_all()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        (await UploadAsync(session, party.FileItemId, "one.pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadAsync(session, party.FileItemId, "two.pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadAsync(session, party.MultiItemId, "a.pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadAsync(session, party.MultiItemId, "b.pdf")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await FilesAsync(party.FileItemId)).Select(f => f.Status)
            .Should().BeEquivalentTo([StoredFileStatus.Superseded, StoredFileStatus.Ready]);
        (await FilesAsync(party.MultiItemId)).Should().OnlyContain(f => f.Status == StoredFileStatus.Ready);

        var view = await JsonOf(await session.GetAsync("/api/portal/invite"));
        var items = view.GetProperty("items").EnumerateArray().ToList();
        items.Single(i => i.GetProperty("id").GetGuid() == party.FileItemId).GetProperty("files").GetArrayLength().Should().Be(1);
        items.Single(i => i.GetProperty("id").GetGuid() == party.MultiItemId).GetProperty("files").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Unsupported_oversized_and_missing_uploads_are_refused_with_friendly_codes()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);
        var url = $"/api/portal/items/{party.FileItemId}/upload-slot";

        var exe = await session.PostAsJsonAsync(url, new { fileName = "a.exe", contentType = "application/x-msdownload", sizeBytes = 10 });
        exe.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOfAsync(exe)).Should().Be("unsupported_type");

        var big = await session.PostAsJsonAsync(url, new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 26L * 1024 * 1024 });
        (await CodeOfAsync(big)).Should().Be("too_large");

        var slot = await session.PostAsJsonAsync(url, new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 });
        var fileId = (await JsonOf(slot)).GetProperty("fileId").GetGuid();
        var notUploaded = await session.PostAsJsonAsync($"/api/portal/items/{party.FileItemId}/complete", new { fileId });
        notUploaded.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOfAsync(notUploaded)).Should().Be("not_uploaded");
    }

    [Fact]
    public async Task A_form_item_does_not_take_files_and_a_file_item_does_not_take_a_field_value()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        (await session.PostAsJsonAsync($"/api/portal/items/{party.NumberItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await session.PutAsJsonAsync($"/api/portal/items/{party.FileItemId}/field", new { value = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- fields and notes ----

    [Fact]
    public async Task Form_fields_are_validated_by_type_saved_and_audited_without_the_value()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        var bad = await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "twelve" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").TryGetProperty("value", out _).Should().BeTrue();
        (await session.PutAsJsonAsync($"/api/portal/items/{party.YesNoItemId}/field", new { value = "maybe" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "12.5" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.PutAsJsonAsync($"/api/portal/items/{party.YesNoItemId}/field", new { value = "yes" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await InviteRowAsync(party.Invite.Id);
        row.Items.Single(i => i.Id == party.NumberItemId).FieldValue.Should().Be("12.5");
        row.Items.Single(i => i.Id == party.NumberItemId).Status.Should().Be(RequestItemStatus.Submitted);
        row.Items.Single(i => i.Id == party.YesNoItemId).FieldValue.Should().Be("Yes");

        var saved = (await AuditEventsAsync()).Where(e => e.Action == "portal.field_saved").ToList();
        saved.Should().HaveCount(2).And.OnlyContain(e => e.ActorType == ActorType.Party && e.ActorId == party.ContactId);
        saved.Should().OnlyContain(e => !e.DetailsJson.Contains("12.5") && !e.DetailsJson.Contains("Yes"));

        // Clearing a saved answer puts the item back to outstanding.
        (await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = (await InviteRowAsync(party.Invite.Id)).Items.Single(i => i.Id == party.NumberItemId);
        cleared.FieldValue.Should().BeNull();
        cleared.Status.Should().Be(RequestItemStatus.Outstanding);
    }

    [Fact]
    public async Task Notes_are_saved_trimmed_and_audited_without_the_text()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        (await session.PutAsJsonAsync($"/api/portal/items/{party.FileItemId}/note", new { note = "  Sent by post too  " }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await InviteRowAsync(party.Invite.Id);
        row.Items.Single(i => i.Id == party.FileItemId).PartyNote.Should().Be("Sent by post too");
        row.Items.Single(i => i.Id == party.FileItemId).Status.Should().Be(RequestItemStatus.Outstanding);
        var audit = (await AuditEventsAsync()).Single(e => e.Action == "portal.note_saved");
        audit.ActorType.Should().Be(ActorType.Party);
        audit.DetailsJson.Should().NotContain("post");

        (await session.PutAsJsonAsync($"/api/portal/items/{party.FileItemId}/note", new { note = new string('x', 2001) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- status and review state ----

    [Fact]
    public async Task Submitting_every_required_item_moves_the_invite_to_submitted()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        (await UploadAsync(session, party.FileItemId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await InviteRowAsync(party.Invite.Id)).Status.Should().Be(CollectionStatus.InProgress);

        (await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "4" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await InviteRowAsync(party.Invite.Id)).Status.Should().Be(CollectionStatus.Submitted);
        (await LoanRowAsync(party.Invite.LoanId)).Status.Should().Be(CollectionStatus.Submitted);
    }

    [Fact]
    public async Task A_rejected_item_shows_its_reason_and_resubmitting_reopens_it_for_review()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);
        (await UploadAsync(session, party.FileItemId)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RequestItems.Where(i => i.Id == party.FileItemId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, RequestItemStatus.Rejected)
                .SetProperty(i => i.RejectionReason, "Page 2 is cut off"));
        }

        var view = await JsonOf(await session.GetAsync("/api/portal/invite"));
        var item = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == party.FileItemId);
        item.GetProperty("status").GetString().Should().Be("Rejected");
        item.GetProperty("rejectionReason").GetString().Should().Be("Page 2 is cut off");

        (await UploadAsync(session, party.FileItemId, "fixed.pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = (await InviteRowAsync(party.Invite.Id)).Items.Single(i => i.Id == party.FileItemId);
        after.Status.Should().Be(RequestItemStatus.Submitted);
        after.RejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task An_approved_item_is_locked()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RequestItems.Where(i => i.Id == party.NumberItemId || i.Id == party.FileItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, RequestItemStatus.Approved));
        }

        var field = await session.PutAsJsonAsync($"/api/portal/items/{party.NumberItemId}/field", new { value = "9" });
        field.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOfAsync(field)).Should().Be("item_locked");
        (await session.PostAsJsonAsync($"/api/portal/items/{party.FileItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await session.PutAsJsonAsync($"/api/portal/items/{party.FileItemId}/note", new { note = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- isolation ----

    [Fact]
    public async Task A_session_never_reaches_another_invites_data()
    {
        var officer = await OfficerAsync();
        var alice = await SendInviteAsync(officer, lastName: "Alice");
        var bob = await SendInviteAsync(officer, lastName: "Bob");
        var aliceSession = await SessionAsync(alice.Token);
        var bobSession = await SessionAsync(bob.Token);

        (await UploadAsync(bobSession, bob.FileItemId, "bob-secret.pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await bobSession.PutAsJsonAsync($"/api/portal/items/{bob.NumberItemId}/field", new { value = "4242424.25" })).EnsureSuccessStatusCode();
        (await bobSession.PutAsJsonAsync($"/api/portal/items/{bob.YesNoItemId}/note", new { note = "bob note" })).EnsureSuccessStatusCode();
        var bobFile = (await FilesAsync(bob.FileItemId)).Single();

        // Alice's view holds nothing of Bob's.
        var text = await (await aliceSession.GetAsync("/api/portal/invite")).Content.ReadAsStringAsync();
        text.Should().NotContain("bob-secret").And.NotContain("bob note").And.NotContain("4242424")
            .And.NotContain(bob.FileItemId.ToString()).And.NotContain(bob.Invite.Id.ToString())
            .And.NotContain(bobFile.Id.ToString());

        // Alice cannot act on Bob's items or files: every attempt looks like a missing item.
        (await aliceSession.PostAsJsonAsync($"/api/portal/items/{bob.FileItemId}/upload-slot",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 10 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await aliceSession.PostAsJsonAsync($"/api/portal/items/{bob.FileItemId}/complete", new { fileId = bobFile.Id }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await aliceSession.PutAsJsonAsync($"/api/portal/items/{bob.NumberItemId}/field", new { value = "1" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await aliceSession.PutAsJsonAsync($"/api/portal/items/{bob.NumberItemId}/note", new { note = "hacked" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Alice cannot complete Bob's file through her own item either.
        (await aliceSession.PostAsJsonAsync($"/api/portal/items/{alice.FileItemId}/complete", new { fileId = bobFile.Id }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bobRow = await InviteRowAsync(bob.Invite.Id);
        bobRow.Items.Single(i => i.Id == bob.NumberItemId).FieldValue.Should().Be("4242424.25");
        bobRow.Items.Single(i => i.Id == bob.NumberItemId).PartyNote.Should().BeNull();
    }
}

public class PortalRateLimitTests : PortalTestBase
{
    protected override int RateLimit => 3;

    [Fact]
    public async Task Token_exchange_attempts_are_rate_limited()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer, password: "correct-horse");

        for (var i = 0; i < 3; i++)
        {
            (await ExchangeAsync(party.Token, "wrong-password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var limited = await ExchangeAsync(party.Token, "correct-horse");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await CodeOfAsync(limited)).Should().Be("rate_limited");
    }
}

public class PortalFieldTests
{
    [Theory]
    [InlineData(FieldType.Text, "  hello ", "hello")]
    [InlineData(FieldType.Number, "1,250.50", "1250.50")]
    [InlineData(FieldType.Date, "2026-03-09", "2026-03-09")]
    [InlineData(FieldType.YesNo, "TRUE", "Yes")]
    [InlineData(FieldType.YesNo, "no", "No")]
    [InlineData(FieldType.Number, "   ", null)]
    public void Answers_are_normalised(FieldType type, string raw, string? expected)
    {
        var (value, error) = PortalService.NormaliseField(type, raw);
        error.Should().BeNull();
        value.Should().Be(expected);
    }

    [Theory]
    [InlineData(FieldType.Number, "abc")]
    [InlineData(FieldType.Date, "09/03/2026")]
    [InlineData(FieldType.Date, "2026-02-31")]
    [InlineData(FieldType.YesNo, "perhaps")]
    public void Invalid_answers_are_refused(FieldType type, string raw)
    {
        PortalService.NormaliseField(type, raw).Error.Should().NotBeNull();
    }
}

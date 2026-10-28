using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Messaging;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Portal;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Messaging;

public class MessagingTests : PortalTestBase
{
    private const string NoteText = "Internal-only: borrower seems unreliable";

    private static object Body(string text) => new { body = text };

    private static async Task<PagedResponse<T>> PageOf<T>(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResponse<T>>())!;
    }

    private async Task<List<OutboxEmail>> EmailsToAsync(string address)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxEmails.AsNoTracking().Where(e => e.To == address).OrderBy(e => e.CreatedAt).ToListAsync();
    }

    private async Task<int> EmailCountAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxEmails.CountAsync();
    }

    // ---- team to party ----

    [Fact]
    public async Task A_team_message_reaches_the_party_with_a_fresh_link_and_is_audited_without_its_text()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);

        var posted = await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body("Please send the 2023 return"));

        posted.StatusCode.Should().Be(HttpStatusCode.Created);
        var message = (await posted.Content.ReadFromJsonAsync<StaffMessageResponse>())!;
        message.AuthorType.Should().Be(MessageAuthorType.Team);
        message.IsInternal.Should().BeFalse();
        message.AuthorName.Should().Be("Test lo@example.com");

        // The party is notified with a new link; the email never carries the message text.
        var newToken = await TokenForAsync(party.ContactId);
        newToken.Should().NotBe(party.Token);
        var mail = (await EmailsToAsync(party.Invite.PartyEmail)).Last();
        mail.Subject.Should().StartWith("New message about");
        (mail.TextBody + mail.HtmlBody).Should().NotContain("2023 return");

        var session = await SessionAsync(newToken);
        var thread = await PageOf<PortalMessageResponse>(await session.GetAsync("/api/portal/messages"));
        thread.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { AuthorType = MessageAuthorType.Team, Body = "Please send the 2023 return" },
            o => o.Including(m => m.AuthorType).Including(m => m.Body));

        var audit = (await AuditEventsAsync()).Single(e => e.Action == "message.posted");
        audit.TargetId.Should().Be(party.Invite.Id);
        audit.DetailsJson.Should().Contain(message.Id.ToString()).And.Contain("Team").And.NotContain("2023");
    }

    [Fact]
    public async Task A_team_message_ends_the_session_opened_with_the_old_link()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var oldSession = await SessionAsync(party.Token);

        (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body("Hello"))).StatusCode
            .Should().Be(HttpStatusCode.Created);

        (await oldSession.GetAsync("/api/portal/messages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_message_to_a_revoked_invite_is_refused_and_an_unknown_invite_is_404()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        (await officer.PostAsync($"/api/invites/{party.Invite.Id}/revoke", null)).EnsureSuccessStatusCode();

        (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body("Hi"))).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await officer.PostAsJsonAsync($"/api/invites/{Guid.NewGuid()}/messages", Body("Hi"))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await officer.GetAsync($"/api/invites/{Guid.NewGuid()}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- party to team ----

    [Fact]
    public async Task A_party_message_is_visible_to_staff_and_emails_the_loan_creator()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);

        var posted = await session.PostAsJsonAsync("/api/portal/messages", Body("Where do I upload the lease?"));

        posted.StatusCode.Should().Be(HttpStatusCode.Created);
        var staffView = await PageOf<StaffMessageResponse>(await officer.GetAsync($"/api/invites/{party.Invite.Id}/messages"));
        staffView.Items.Should().ContainSingle().Which.AuthorType.Should().Be(MessageAuthorType.Party);
        staffView.Items[0].Body.Should().Be("Where do I upload the lease?");

        var mail = (await EmailsToAsync("lo@example.com")).Single();
        mail.Subject.Should().StartWith($"New message from {party.Invite.PartyName}");
        mail.TextBody.Should().Contain($"/loans/{party.Invite.LoanId}").And.NotContain("lease").And.NotContain("/p/");

        var audit = (await AuditEventsAsync()).Single(e => e.Action == "message.posted");
        audit.ActorType.Should().Be(ActorType.Party);
        audit.DetailsJson.Should().Contain("Party").And.NotContain("lease");
    }

    [Fact]
    public async Task A_party_must_accept_the_privacy_notice_before_posting()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token, acceptPrivacy: false);

        (await session.PostAsJsonAsync("/api/portal/messages", Body("Hi"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await session.GetAsync("/api/portal/messages")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Empty_and_oversized_messages_are_rejected_on_both_sides()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var session = await SessionAsync(party.Token);
        var tooLong = new string('x', MessageLimits.MaxBodyLength + 1);

        foreach (var text in new[] { "", "   ", tooLong })
        {
            (await session.PostAsJsonAsync("/api/portal/messages", Body(text))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body(text))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body(text))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await session.PostAsJsonAsync("/api/portal/messages", new { body = (string?)null })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_party_sees_only_the_thread_of_their_own_invite()
    {
        var officer = await OfficerAsync();
        var first = await SendInviteAsync(officer);
        var second = await SendInviteAsync(officer);
        (await officer.PostAsJsonAsync($"/api/invites/{first.Invite.Id}/messages", Body("For the first party"))).EnsureSuccessStatusCode();
        (await officer.PostAsJsonAsync($"/api/invites/{second.Invite.Id}/messages", Body("For the second party"))).EnsureSuccessStatusCode();

        var session = await SessionAsync(await TokenForAsync(second.ContactId));
        var thread = await PageOf<PortalMessageResponse>(await session.GetAsync("/api/portal/messages"));

        thread.Items.Should().ContainSingle().Which.Body.Should().Be("For the second party");
    }

    // ---- internal notes ----

    [Fact]
    public async Task Internal_notes_are_returned_to_staff_but_never_by_any_portal_endpoint()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var emailsBefore = await EmailCountAsync();

        var note = await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body(NoteText));
        note.StatusCode.Should().Be(HttpStatusCode.Created);
        (await note.Content.ReadFromJsonAsync<StaffMessageResponse>())!.IsInternal.Should().BeTrue();
        (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body("Visible to all"))).EnsureSuccessStatusCode();
        (await EmailCountAsync()).Should().Be(emailsBefore + 1, "a note sends no email, the message sends one");

        var staffView = await PageOf<StaffMessageResponse>(await officer.GetAsync($"/api/invites/{party.Invite.Id}/messages"));
        staffView.Items.Select(m => m.Body).Should().BeEquivalentTo(NoteText, "Visible to all");

        var session = await SessionAsync(await TokenForAsync(party.ContactId));
        var portalCalls = new[] { "/api/portal/messages", "/api/portal/messages?pageSize=100", "/api/portal/invite" };
        foreach (var path in portalCalls)
        {
            var response = await session.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().NotContain(NoteText).And.NotContain("unreliable", path);
            if (path.Contains("messages"))
            {
                text.Should().Contain("Visible to all").And.NotContainEquivalentOf("isInternal");
            }
        }

        // The party cannot reach the staff endpoints, with or without the note.
        (await session.GetAsync($"/api/invites/{party.Invite.Id}/messages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await session.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body("x"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_note_is_audited_without_its_text()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);

        (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body(NoteText))).EnsureSuccessStatusCode();

        var audit = (await AuditEventsAsync()).Single(e => e.Action == "note.posted");
        audit.TargetId.Should().Be(party.Invite.Id);
        audit.DetailsJson.Should().Contain("Internal").And.NotContain("unreliable");
    }

    // ---- roles ----

    [Fact]
    public async Task A_viewer_can_read_the_thread_but_not_post_or_see_notes()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body("Shared"))).EnsureSuccessStatusCode();
        (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body(NoteText))).EnsureSuccessStatusCode();
        var viewer = await ClientForAsync("viewer@example.com", [StaffRoles.Viewer]);

        var thread = await PageOf<StaffMessageResponse>(await viewer.GetAsync($"/api/invites/{party.Invite.Id}/messages"));
        thread.Items.Should().ContainSingle().Which.Body.Should().Be("Shared");

        (await viewer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/messages", Body("Hi"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body("Hi"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_auditor_and_an_anonymous_caller_cannot_use_the_thread()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        var auditor = await ClientForAsync("auditor@example.com", [StaffRoles.Auditor]);
        var url = $"/api/invites/{party.Invite.Id}/messages";

        (await auditor.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await auditor.PostAsJsonAsync(url, Body("Hi"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await NewClient().PostAsJsonAsync(url, Body("Hi"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await NewClient().GetAsync("/api/portal/messages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- paging ----

    [Fact]
    public async Task The_thread_is_paged_newest_first()
    {
        var officer = await OfficerAsync();
        var party = await SendInviteAsync(officer);
        for (var i = 1; i <= 3; i++)
        {
            (await officer.PostAsJsonAsync($"/api/invites/{party.Invite.Id}/notes", Body($"Note {i}"))).EnsureSuccessStatusCode();
        }

        var first = await PageOf<StaffMessageResponse>(await officer.GetAsync($"/api/invites/{party.Invite.Id}/messages?page=1&pageSize=2"));
        var second = await PageOf<StaffMessageResponse>(await officer.GetAsync($"/api/invites/{party.Invite.Id}/messages?page=2&pageSize=2"));

        first.Total.Should().Be(3);
        first.Items.Select(m => m.Body).Should().Equal("Note 3", "Note 2");
        second.Items.Select(m => m.Body).Should().Equal("Note 1");
        (await officer.GetAsync($"/api/invites/{party.Invite.Id}/messages?pageSize=101")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

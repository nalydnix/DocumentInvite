using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Contacts;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Loans;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Loans;

public partial class LoansAndInvitesTests(SqlServerFixture sql) : StaffTestBase(sql)
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TestClock _clock = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<TimeProvider>(_clock);
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.UtcDateTime);

    private static LoanRequest ValidLoan(string reference = "LN-1001") =>
        new(reference, "Acme acquisition", 1_250_000.50m, "SBA 7(a)");

    private static InviteItemRequest File(string name = "Tax return", bool required = true) =>
        new(name, ItemKind.FileUpload, "Last two years", AllowMultiple: false, FieldType: null, IsRequired: required);

    private static InviteItemRequest Field(string name = "Years in business") =>
        new(name, ItemKind.FormField, "", AllowMultiple: false, FieldType: Api.Domain.FieldType.Number, IsRequired: true);

    private async Task<HttpClient> OfficerAsync() => await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

    private static async Task<LoanResponse> CreateLoanAsync(HttpClient client, LoanRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/api/loans", request ?? ValidLoan());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<LoanResponse>())!;
    }

    private static async Task<ContactResponse> CreateContactAsync(
        HttpClient client, string first = "Ann", string email = "ann@example.com")
    {
        var response = await client.PostAsJsonAsync("/api/contacts",
            new ContactRequest(first, "Lee", email, null, PartyType.Borrower));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContactResponse>())!;
    }

    private SendPartyRequest Party(Guid contactId, int? interval = null, params InviteItemRequest[] items) =>
        new(contactId, Today.AddDays(10), interval, items.Length == 0 ? [File(), Field()] : items);

    private static async Task<IReadOnlyList<InviteResponse>> SendAsync(
        HttpClient client, Guid loanId, params SendPartyRequest[] parties)
    {
        var response = await client.PostAsJsonAsync($"/api/loans/{loanId}/invites", new SendInvitesRequest(parties));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<InviteResponse>>())!;
    }

    private async Task<List<OutboxEmail>> EmailsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxEmails.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
    }

    private static string TokenIn(OutboxEmail email) => LinkPattern().Match(email.TextBody).Groups[1].Value;

    [GeneratedRegex(@"/p/([A-Za-z0-9_-]+)")]
    private static partial Regex LinkPattern();

    private async Task<TokenLookupResult> ResolveAsync(string token)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InviteTokenService>().ResolveAsync(token);
    }

    private async Task<Invite> InviteRowAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Invites.AsNoTracking().Include(i => i.Items).SingleAsync(i => i.Id == id);
    }

    private static async Task<List<string>> ErrorFieldsAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
    }

    // ---- authorisation ----

    [Fact]
    public async Task Viewer_can_read_but_not_write()
    {
        var officer = await OfficerAsync();
        var contact = await CreateContactAsync(officer);
        var loan = await CreateLoanAsync(officer);
        var invite = (await SendAsync(officer, loan.Id, Party(contact.Id)))[0];
        var viewer = await ClientForAsync("v@example.com", [StaffRoles.Viewer]);

        (await viewer.GetAsync("/api/loans")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/loans/{loan.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/invites/{invite.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync("/api/loans", ValidLoan("LN-2"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync($"/api/loans/{loan.Id}/invites", new SendInvitesRequest([Party(contact.Id)])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/invites/{invite.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/invites/{invite.Id}/reissue", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Auditors_and_anonymous_callers_are_rejected()
    {
        var auditor = await ClientForAsync("aud@example.com", [StaffRoles.Auditor]);

        (await auditor.GetAsync("/api/loans")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().GetAsync("/api/loans")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await NewClient().PostAsJsonAsync("/api/loans", ValidLoan())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await NewClient().PostAsync($"/api/invites/{Guid.NewGuid()}/revoke", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- loans ----

    [Fact]
    public async Task A_loan_can_be_created_listed_and_viewed()
    {
        var client = await OfficerAsync();

        var created = await CreateLoanAsync(client);
        var fetched = await client.GetFromJsonAsync<LoanResponse>($"/api/loans/{created.Id}");
        var page = await client.GetFromJsonAsync<LoanPage>("/api/loans");

        fetched!.Reference.Should().Be("LN-1001");
        fetched.Name.Should().Be("Acme acquisition");
        fetched.Amount.Should().Be(1_250_000.50m);
        fetched.LoanType.Should().Be("SBA 7(a)");
        fetched.Status.Should().Be(CollectionStatus.Sent);
        fetched.Invites.Should().BeEmpty();
        page!.Total.Should().Be(1);
        page.Items.Single().Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task The_list_can_be_searched_and_filtered_by_status()
    {
        var client = await OfficerAsync();
        await CreateLoanAsync(client, ValidLoan("AAA-1"));
        await CreateLoanAsync(client, ValidLoan("BBB-2"));

        (await client.GetFromJsonAsync<LoanPage>("/api/loans?search=bbb"))!.Items.Select(l => l.Reference)
            .Should().Equal("BBB-2");
        (await client.GetFromJsonAsync<LoanPage>("/api/loans?status=Completed"))!.Total.Should().Be(0);
        (await client.GetFromJsonAsync<LoanPage>("/api/loans?status=Sent"))!.Total.Should().Be(2);
    }

    [Fact]
    public async Task Loan_references_are_unique()
    {
        var client = await OfficerAsync();
        await CreateLoanAsync(client);

        var duplicate = await client.PostAsJsonAsync("/api/loans", ValidLoan("ln-1001"));

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Creating_a_loan_validates_its_fields()
    {
        var client = await OfficerAsync();

        var empty = await client.PostAsJsonAsync("/api/loans", new { });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(empty)).Should().Contain(["reference", "name", "amount", "loanType"]);

        foreach (var bad in new[]
        {
            new LoanRequest("LN 1", "n", 10, "t"),
            new LoanRequest("LN-1", "n", 0, "t"),
            new LoanRequest("LN-1", "n", -5, "t"),
            new LoanRequest("LN-1", "n", 10.123m, "t"),
        })
        {
            (await client.PostAsJsonAsync("/api/loans", bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Getting_an_unknown_loan_is_a_404()
    {
        var client = await OfficerAsync();

        (await client.GetAsync($"/api/loans/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- sending ----

    [Fact]
    public async Task Sending_creates_one_invite_per_party_with_the_selected_items()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var bob = await CreateContactAsync(client, "Bob", "bob@example.com");
        var loan = await CreateLoanAsync(client);

        var invites = await SendAsync(client, loan.Id,
            Party(ann.Id, 3, File("Edited name", required: true), File("Optional extra", required: false)),
            Party(bob.Id, null, Field()));

        invites.Should().HaveCount(2);
        var annInvite = invites.Single(i => i.ContactId == ann.Id);
        annInvite.PartyName.Should().Be("Ann Lee");
        annInvite.ReminderIntervalDays.Should().Be(3);
        annInvite.DueDate.Should().Be(Today.AddDays(10));
        annInvite.Status.Should().Be(CollectionStatus.Sent);
        annInvite.Items.Select(i => i.Name).Should().Equal("Edited name", "Optional extra");
        annInvite.Items.Select(i => i.IsRequired).Should().Equal(true, false);
        annInvite.Items.Should().OnlyContain(i => i.Status == RequestItemStatus.Outstanding);
        invites.Single(i => i.ContactId == bob.Id).ReminderIntervalDays.Should().Be(7, "the default comes from the settings");

        var detail = await client.GetFromJsonAsync<LoanResponse>($"/api/loans/{loan.Id}");
        detail!.Status.Should().Be(CollectionStatus.Sent);
        detail.Invites.Should().HaveCount(2);
        (await client.GetFromJsonAsync<LoanPage>("/api/loans"))!.Items.Single().PartyCount.Should().Be(2);
    }

    [Fact]
    public async Task Sending_emails_each_party_its_portal_link()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var bob = await CreateContactAsync(client, "Bob", "bob@example.com");
        var loan = await CreateLoanAsync(client);

        await SendAsync(client, loan.Id, Party(ann.Id), Party(bob.Id));

        var emails = await EmailsAsync();
        emails.Select(e => e.To).Should().BeEquivalentTo("ann@example.com", "bob@example.com");
        emails.Should().OnlyContain(e => e.TextBody.Contains("http://localhost:5173/p/") && e.HtmlBody.Contains("/p/"));
        TokenIn(emails[0]).Should().NotBe(TokenIn(emails[1]));
    }

    [Fact]
    public async Task Only_the_sha256_hash_of_the_token_is_stored()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);

        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];

        var token = TokenIn((await EmailsAsync()).Single());
        token.Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 random bytes in base64url");
        var row = await InviteRowAsync(invite.Id);
        row.TokenHash.Should().Equal(PortalTokens.Hash(token));
        row.TokenHash.Should().HaveCount(32);
        JsonSerializer.Serialize(invite).Should().NotContain(token, "the link is only ever emailed");
        (await ResolveAsync(token)).Invite!.Id.Should().Be(invite.Id);
    }

    [Fact]
    public async Task The_link_expires_thirty_days_after_the_due_date()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var due = new DateOnly(2030, 6, 1);


        var invite = (await SendAsync(client, loan.Id, new SendPartyRequest(ann.Id, due, null, [File()])))[0];

        invite.TokenExpiresAt.Should().Be(new DateTimeOffset(2030, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var token = TokenIn((await EmailsAsync()).Single());
        _clock.Now = new DateTimeOffset(2030, 6, 30, 23, 0, 0, TimeSpan.Zero);
        (await ResolveAsync(token)).IsValid.Should().BeTrue();
        _clock.Now = new DateTimeOffset(2030, 7, 1, 0, 0, 1, TimeSpan.Zero);
        (await ResolveAsync(token)).Failure.Should().Be(TokenLookupFailure.Expired);
    }

    [Fact]
    public async Task Unknown_tokens_do_not_resolve()
    {
        (await ResolveAsync(PortalTokens.Generate())).Failure.Should().Be(TokenLookupFailure.NotFound);
        (await ResolveAsync("")).Failure.Should().Be(TokenLookupFailure.NotFound);
    }

    [Fact]
    public async Task Sending_is_audited_with_ids_only()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);

        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];

        var events = await AuditEventsAsync();
        events.Should().Contain(e => e.Action == "loan.created" && e.TargetId == loan.Id);
        var sent = events.Single(e => e.Action == "invite.sent");
        sent.TargetType.Should().Be("invite");
        sent.TargetId.Should().Be(invite.Id);
        sent.DetailsJson.Should().Contain(loan.Id.ToString()).And.Contain(ann.Id.ToString());
        foreach (var e in events.Where(e => e.Action is "loan.created" or "invite.sent"))
        {
            e.DetailsJson.Should().NotContainAny("Ann", "Lee", "example.com", "Acme", "LN-1001", "Tax return");
            e.ActorId.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_party_cannot_be_invited_twice_to_the_same_loan()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        await SendAsync(client, loan.Id, Party(ann.Id));

        var again = await client.PostAsJsonAsync($"/api/loans/{loan.Id}/invites", new SendInvitesRequest([Party(ann.Id)]));

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await EmailsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_rejected_request_creates_nothing()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);

        var response = await client.PostAsJsonAsync($"/api/loans/{loan.Id}/invites",
            new SendInvitesRequest([Party(ann.Id), Party(Guid.NewGuid())]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(response)).Should().Contain("parties[1].contactId");
        (await client.GetFromJsonAsync<LoanResponse>($"/api/loans/{loan.Id}"))!.Invites.Should().BeEmpty();
        (await EmailsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Sending_validates_the_request()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var url = $"/api/loans/{loan.Id}/invites";

        (await client.PostAsJsonAsync(url, new SendInvitesRequest([]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new SendInvitesRequest([new SendPartyRequest(ann.Id, Today.AddDays(1), null, [])])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new SendInvitesRequest([new SendPartyRequest(ann.Id, Today.AddDays(1), 0, [File()])])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new SendInvitesRequest([new SendPartyRequest(ann.Id, null, null, [File()])])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new SendInvitesRequest([Party(ann.Id), Party(ann.Id)])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new SendInvitesRequest(
            [new SendPartyRequest(ann.Id, Today.AddDays(1), null, [new InviteItemRequest("", ItemKind.FileUpload, "", false, null, true)])])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var past = await client.PostAsJsonAsync(url, new SendInvitesRequest(
            [new SendPartyRequest(ann.Id, Today.AddDays(-1), null, [File()])]));
        past.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(past)).Should().Contain("parties[0].dueDate");

        (await client.PostAsJsonAsync($"/api/loans/{Guid.NewGuid()}/invites", new SendInvitesRequest([Party(ann.Id)])))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- revoking and reissuing ----

    [Fact]
    public async Task Revoking_stops_the_link_working_and_is_audited()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];
        var token = TokenIn((await EmailsAsync()).Single());

        var response = await client.PostAsync($"/api/invites/{invite.Id}/revoke", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<InviteResponse>())!.RevokedAt.Should().NotBeNull();
        (await ResolveAsync(token)).Failure.Should().Be(TokenLookupFailure.Revoked);
        (await AuditEventsAsync()).Should().ContainSingle(e => e.Action == "invite.revoked" && e.TargetId == invite.Id);
        (await client.PostAsync($"/api/invites/{invite.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Reissuing_replaces_the_link_and_the_old_one_stops_working()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];
        var oldToken = TokenIn((await EmailsAsync()).Single());
        await client.PostAsync($"/api/invites/{invite.Id}/revoke", null);

        var response = await client.PostAsync($"/api/invites/{invite.Id}/reissue", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<InviteResponse>())!.RevokedAt.Should().BeNull();
        var emails = await EmailsAsync();
        emails.Should().HaveCount(2);
        var newToken = emails.Select(TokenIn).Single(t => t != oldToken);
        newToken.Should().NotBe(oldToken);
        (await ResolveAsync(oldToken)).Failure.Should().Be(TokenLookupFailure.NotFound);
        (await ResolveAsync(newToken)).Invite!.Id.Should().Be(invite.Id);
        (await InviteRowAsync(invite.Id)).TokenHash.Should().Equal(PortalTokens.Hash(newToken));
        (await AuditEventsAsync()).Should().ContainSingle(e => e.Action == "invite.reissued" && e.TargetId == invite.Id);
    }

    [Fact]
    public async Task Reissuing_an_active_invite_also_invalidates_the_old_link()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];
        var oldToken = TokenIn((await EmailsAsync()).Single());

        (await client.PostAsync($"/api/invites/{invite.Id}/reissue", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ResolveAsync(oldToken)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_reissued_link_gets_a_fresh_expiry_when_the_due_date_is_long_past()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var longAgo = Today.AddDays(-100);
            await db.Invites.ExecuteUpdateAsync(s => s.SetProperty(i => i.DueDate, longAgo));
        }

        var reissued = await (await client.PostAsync($"/api/invites/{invite.Id}/reissue", null))
            .Content.ReadFromJsonAsync<InviteResponse>();

        reissued!.TokenExpiresAt.Should().Be(_clock.Now.AddDays(30));
    }

    [Fact]
    public async Task A_completed_invite_cannot_be_reissued()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var loan = await CreateLoanAsync(client);
        var invite = (await SendAsync(client, loan.Id, Party(ann.Id)))[0];
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Invites.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, CollectionStatus.Completed));
        }

        (await client.PostAsync($"/api/invites/{invite.Id}/reissue", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unknown_invites_are_a_404()
    {
        var client = await OfficerAsync();

        (await client.PostAsync($"/api/invites/{Guid.NewGuid()}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/invites/{Guid.NewGuid()}/reissue", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/invites/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- status ----

    [Fact]
    public async Task The_loan_status_follows_its_active_invites_and_stamps_CompletedAt()
    {
        var client = await OfficerAsync();
        var ann = await CreateContactAsync(client);
        var bob = await CreateContactAsync(client, "Bob", "bob@example.com");
        var loan = await CreateLoanAsync(client);
        var invites = await SendAsync(client, loan.Id, Party(ann.Id, null, File()), Party(bob.Id, null, File()));
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RequestItems.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, RequestItemStatus.Approved));
        }

        // Revoking triggers a recalculation: Bob's invite is excluded, Ann's is approved, so the loan completes.
        var bobInvite = invites.Single(i => i.ContactId == bob.Id);
        await client.PostAsync($"/api/invites/{bobInvite.Id}/revoke", null);

        var detail = await client.GetFromJsonAsync<LoanResponse>($"/api/loans/{loan.Id}");
        detail!.Status.Should().Be(CollectionStatus.Completed);
        detail.CompletedAt.Should().NotBeNull();
        detail.Invites.Single(i => i.ContactId == ann.Id).Status.Should().Be(CollectionStatus.Completed);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Contacts;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Contacts;

public class ContactsTests : StaffTestBase
{
    private static ContactRequest Valid(
        string first = "Ann", string last = "Lee", string email = "ann.lee@example.com",
        string? phone = "+1 (555) 010-2030", PartyType type = PartyType.Borrower) =>
        new(first, last, email, phone, type);

    private async Task<ContactResponse> SeedAsync(HttpClient client, ContactRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/contacts", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContactResponse>())!;
    }

    // ---- authorisation ----

    [Theory]
    [InlineData(StaffRoles.Admin)]
    [InlineData(StaffRoles.LoanOfficer)]
    public async Task Admin_and_loan_officer_can_write(string role)
    {
        var client = await ClientForAsync("w@example.com", [role]);

        var created = await SeedAsync(client, Valid());
        var update = await client.PutAsJsonAsync($"/api/contacts/{created.Id}", Valid(first: "Anna"));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<ContactResponse>())!.FirstName.Should().Be("Anna");
    }

    [Fact]
    public async Task Auditor_cannot_use_the_contact_api()
    {
        var auditor = await ClientForAsync("aud@example.com", [StaffRoles.Auditor]);

        (await auditor.GetAsync("/api/contacts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await auditor.PostAsJsonAsync("/api/contacts", Valid())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var client = NewClient();

        (await client.GetAsync("/api/contacts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/contacts", Valid())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- validation ----

    [Fact]
    public async Task Create_rejects_missing_required_fields()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.PostAsJsonAsync("/api/contacts", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = await ErrorFieldsAsync(response);
        errors.Should().Contain(["firstName", "lastName", "email", "partyType"]);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@")]
    [InlineData("@example.com")]
    public async Task Create_rejects_an_invalid_email(string email)
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.PostAsJsonAsync("/api/contacts", Valid(email: email));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(response)).Should().Contain("email");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("555-CALL-NOW")]
    [InlineData("1234567890123456")]
    [InlineData("++15550102030")]
    public async Task Create_rejects_an_invalid_phone(string phone)
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.PostAsJsonAsync("/api/contacts", Valid(phone: phone));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(response)).Should().Contain("phone");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("555.010.2030")]
    public async Task Phone_is_optional_and_accepts_common_formats(string? phone)
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var created = await SeedAsync(client, Valid(phone: phone));

        created.Phone.Should().Be(string.IsNullOrEmpty(phone) ? null : phone);
    }

    [Fact]
    public async Task Create_rejects_an_unknown_party_type()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.PostAsJsonAsync("/api/contacts", new
        {
            firstName = "A", lastName = "B", email = "a@example.com", partyType = 99,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_validates_like_create()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var contact = await SeedAsync(client, Valid());

        var response = await client.PutAsJsonAsync($"/api/contacts/{contact.Id}", Valid(email: "bad"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task List_rejects_bad_paging(string query)
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        (await client.GetAsync($"/api/contacts?{query}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- behaviour ----

    [Fact]
    public async Task Create_then_get_round_trips_every_field_and_trims_input()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.PostAsJsonAsync("/api/contacts",
            Valid(first: "  Ann ", last: " Lee ", email: " ann.lee@example.com ", type: PartyType.Guarantor));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<ContactResponse>())!;
        response.Headers.Location!.ToString().Should().EndWith($"/api/contacts/{created.Id}");

        var fetched = await client.GetFromJsonAsync<ContactResponse>($"/api/contacts/{created.Id}");
        fetched.Should().Be(created);
        fetched!.FirstName.Should().Be("Ann");
        fetched.LastName.Should().Be("Lee");
        fetched.Email.Should().Be("ann.lee@example.com");
        fetched.PartyType.Should().Be(PartyType.Guarantor);
        fetched.IsPseudonymised.Should().BeFalse();
    }

    [Fact]
    public async Task Party_types_are_serialised_as_names()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var created = await SeedAsync(client, Valid(type: PartyType.CoBorrower));

        var json = await client.GetStringAsync($"/api/contacts/{created.Id}");

        json.Should().Contain("\"partyType\":\"CoBorrower\"");
    }

    [Fact]
    public async Task Get_and_update_return_404_for_an_unknown_contact()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var id = Guid.NewGuid();

        (await client.GetAsync($"/api/contacts/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/api/contacts/{id}", Valid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_changes_the_stored_contact_and_can_clear_the_phone()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var contact = await SeedAsync(client, Valid());

        var response = await client.PutAsJsonAsync(
            $"/api/contacts/{contact.Id}", Valid("Bea", "Cox", "bea@example.com", null, PartyType.Attorney));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await client.GetFromJsonAsync<ContactResponse>($"/api/contacts/{contact.Id}");
        fetched.Should().BeEquivalentTo(new ContactResponse(
            contact.Id, "Bea", "Cox", "bea@example.com", null, PartyType.Attorney, false));
    }

    [Fact]
    public async Task A_pseudonymised_contact_cannot_be_edited()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var contact = await SeedAsync(client, Valid());
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Contacts.Where(c => c.Id == contact.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsPseudonymised, true));
        }

        var response = await client.PutAsJsonAsync($"/api/contacts/{contact.Id}", Valid(first: "New"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_searches_name_and_email_and_pages_in_name_order()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        await SeedAsync(client, Valid("Ann", "Lee", "ann@alpha.test"));
        await SeedAsync(client, Valid("Bob", "Adams", "bob@beta.test"));
        await SeedAsync(client, Valid("Cara", "Zane", "cara@alpha.test"));
        await SeedAsync(client, Valid("Dan", "100%_Real", "dan@gamma.test"));

        var all = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>("/api/contacts"))!;
        all.Total.Should().Be(4);
        all.Items.Select(i => i.LastName).Should().Equal("100%_Real", "Adams", "Lee", "Zane");

        var byEmail = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>("/api/contacts?search=alpha"))!;
        byEmail.Items.Select(i => i.FirstName).Should().BeEquivalentTo("Ann", "Cara");

        var byFullName = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>("/api/contacts?search=bob%20adams"))!;
        byFullName.Items.Should().ContainSingle().Which.Email.Should().Be("bob@beta.test");

        var wildcard = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>("/api/contacts?search=%25"))!;
        wildcard.Items.Should().ContainSingle().Which.FirstName.Should().Be("Dan");

        var page2 = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>("/api/contacts?page=2&pageSize=3"))!;
        page2.Total.Should().Be(4);
        page2.Items.Should().ContainSingle().Which.LastName.Should().Be("Zane");
        page2.Page.Should().Be(2);
        page2.PageSize.Should().Be(3);
    }

    // ---- audit ----

    [Fact]
    public async Task Creates_and_updates_are_audited_with_ids_only()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var contact = await SeedAsync(client, Valid("Secret", "Person", "secret.person@example.com", "+1 555 010 2030"));
        await client.PutAsJsonAsync($"/api/contacts/{contact.Id}", Valid("Secret", "Person", "secret.person@example.com", null, PartyType.Broker));

        var events = (await AuditEventsAsync()).Where(e => e.TargetType == "contact").ToList();

        events.Select(e => e.Action).Should().Equal("contact.created", "contact.updated");
        events.Should().OnlyContain(e => e.TargetId == contact.Id && e.ActorType == ActorType.User);
        events[0].DetailsJson.Should().Be("{\"partyType\":\"Borrower\"}");
        events[1].DetailsJson.Should().Be("{\"partyType\":\"Broker\"}");
        events.Should().OnlyContain(e =>
            !e.DetailsJson.Contains("Secret") && !e.DetailsJson.Contains("example.com") && !e.DetailsJson.Contains("555"));
    }

    [Fact]
    public async Task Rejected_requests_write_no_audit_event()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var auditor = await ClientForAsync("a@example.com", [StaffRoles.Auditor]);

        await client.PostAsJsonAsync("/api/contacts", Valid(email: "bad"));
        await auditor.PostAsJsonAsync("/api/contacts", Valid());

        (await AuditEventsAsync()).Should().NotContain(e => e.TargetType == "contact");
    }

    private static async Task<List<string>> ErrorFieldsAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
    }
}

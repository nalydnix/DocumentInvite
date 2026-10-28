using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Templates;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Templates;

public class TemplateTests : StaffTestBase
{
    private Task<HttpClient> OfficerAsync() => ClientForAsync("officer@example.com", [StaffRoles.LoanOfficer]);

    private static TemplateItemRequest Upload(string name, bool required = true, bool multiple = false, Guid? id = null) =>
        new(id, name, ItemKind.FileUpload, "Please upload.", multiple, null, required);

    private static TemplateItemRequest Form(string name, FieldType type, Guid? id = null) =>
        new(id, name, ItemKind.FormField, "", false, type, true);

    private static SaveTemplateRequest Request(string name = "My checklist", params TemplateItemRequest[] items) =>
        new(name, "A description", items);

    private static async Task<TemplateResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<TemplateResponse>())!;

    private static Task<PagedResponse<TemplateSummaryResponse>?> ListAsync(HttpClient client, string query) =>
        client.GetFromJsonAsync<PagedResponse<TemplateSummaryResponse>>($"/api/templates?{query}");

    private async Task SeedAsync(int count, string prefix = "Seed", bool archived = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.Templates.Add(new Template
            {
                Id = Guid.NewGuid(),
                Name = $"{prefix} {i:00}",
                Description = "",
                ArchivedAt = archived ? DateTimeOffset.UtcNow : null,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_template_list_pages_by_name_with_a_partial_last_page()
    {
        var client = await OfficerAsync();
        await SeedAsync(5);

        var first = (await ListAsync(client, "page=1&pageSize=3"))!;
        var last = (await ListAsync(client, "page=3&pageSize=3"))!;

        first.Total.Should().Be(7);
        first.PageSize.Should().Be(3);
        first.Items.Select(t => t.Name).Should().Equal("Commercial real estate purchase", "SBA 7(a) business acquisition", "Seed 00");
        last.Page.Should().Be(3);
        last.Items.Should().ContainSingle().Which.Name.Should().Be("Seed 04");
    }

    [Fact]
    public async Task A_template_page_past_the_end_is_empty_with_the_real_total()
    {
        var client = await OfficerAsync();

        var page = (await ListAsync(client, "page=9&pageSize=2"))!;

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(2);
        page.Page.Should().Be(9);
    }

    [Fact]
    public async Task Templates_with_the_same_name_still_page_without_overlap()
    {
        var client = await OfficerAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 4; i++)
            {
                db.Templates.Add(new Template { Id = Guid.NewGuid(), Name = "Same", Description = "" });
            }
            await db.SaveChangesAsync();
        }

        var one = (await ListAsync(client, "page=1&pageSize=3"))!;
        var two = (await ListAsync(client, "page=2&pageSize=3"))!;

        one.Items.Concat(two.Items).Select(t => t.Id).Distinct().Should().HaveCount(6);
    }

    [Fact]
    public async Task The_archived_filter_applies_across_pages()
    {
        var client = await OfficerAsync();
        await SeedAsync(3, archived: true);

        (await ListAsync(client, "pageSize=2"))!.Total.Should().Be(2);
        var all = (await ListAsync(client, "includeArchived=true&page=2&pageSize=2"))!;
        all.Total.Should().Be(5);
        all.Items.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=abc")]
    public async Task The_template_list_rejects_bad_paging(string query)
    {
        var client = await OfficerAsync();

        (await client.GetAsync($"/api/templates?{query}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_two_lending_templates_are_seeded_with_items_and_form_fields()
    {
        var viewer = await ClientForAsync("viewer@example.com", [StaffRoles.Viewer]);

        var page = await viewer.GetFromJsonAsync<PagedResponse<TemplateSummaryResponse>>("/api/templates");
        var list = page!.Items;

        page.Total.Should().Be(2);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(25);
        list.Select(t => t.Name).Should().BeEquivalentTo(
            "SBA 7(a) business acquisition", "Commercial real estate purchase");
        list.Should().OnlyContain(t => t.IsSeeded && !t.IsArchived && t.ItemCount >= 10);

        foreach (var summary in list)
        {
            var template = await viewer.GetFromJsonAsync<TemplateResponse>($"/api/templates/{summary.Id}");
            template!.Items.Select(i => i.SortOrder).Should().BeInAscendingOrder();
            template.Items.Should().Contain(i => i.Kind == ItemKind.FileUpload && i.AllowMultiple);
            template.Items.Where(i => i.Kind == ItemKind.FormField).Should()
                .NotBeEmpty().And.OnlyContain(i => i.FieldType != null);
            template.Items.Where(i => i.Kind == ItemKind.FileUpload).Should().OnlyContain(i => i.FieldType == null);
        }
    }

    [Fact]
    public async Task Seeding_twice_creates_no_duplicates_and_keeps_staff_edits()
    {
        var officer = await OfficerAsync();
        var sba = await officer.GetFromJsonAsync<TemplateResponse>($"/api/templates/{TemplateSeeder.SbaTemplateId}");
        var edit = Request("Renamed SBA checklist", Upload("Only item"));
        (await officer.PutAsJsonAsync($"/api/templates/{sba!.Id}", edit)).EnsureSuccessStatusCode();

        using (var scope = Factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<TemplateSeeder>();
            await seeder.SeedAsync();
            await seeder.SeedAsync();
        }

        using var verify = Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Templates.CountAsync()).Should().Be(2);
        (await db.Templates.Include(t => t.Items).SingleAsync(t => t.Id == TemplateSeeder.SbaTemplateId))
            .Should().Match<Template>(t => t.Name == "Renamed SBA checklist" && t.Items.Count == 1);
    }

    [Fact]
    public async Task Seeding_recreates_a_missing_template_only()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Templates.Where(t => t.Id == TemplateSeeder.CommercialRealEstateTemplateId).ExecuteDeleteAsync();
            await scope.ServiceProvider.GetRequiredService<TemplateSeeder>().SeedAsync();
        }

        using var verify = Factory.Services.CreateScope();
        (await verify.ServiceProvider.GetRequiredService<AppDbContext>().Templates.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(StaffRoles.Admin)]
    [InlineData(StaffRoles.LoanOfficer)]
    public async Task Staff_can_create_a_template_and_read_it_back_in_order(string role)
    {
        var client = await ClientForAsync("staff@example.com", [role]);
        var request = Request("Equipment loan", Upload("Invoice", multiple: true), Form("Year", FieldType.Number), Upload("Quote", false));

        var response = await client.PostAsJsonAsync("/api/templates", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadAsync(response);
        response.Headers.Location!.ToString().Should().EndWith($"/api/templates/{created.Id}");
        var fetched = await client.GetFromJsonAsync<TemplateResponse>($"/api/templates/{created.Id}");
        fetched!.Items.Select(i => i.Name).Should().Equal("Invoice", "Year", "Quote");
        fetched.Items.Select(i => i.SortOrder).Should().Equal(0, 1, 2);
        fetched.Items[0].AllowMultiple.Should().BeTrue();
        fetched.Items[1].FieldType.Should().Be(FieldType.Number);
        fetched.Items[2].IsRequired.Should().BeFalse();
        fetched.IsSeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Editing_renames_reorders_adds_and_removes_items()
    {
        var client = await OfficerAsync();
        var created = await ReadAsync(await client.PostAsJsonAsync("/api/templates",
            Request("Checklist", Upload("A"), Upload("B"), Upload("C"))));
        var (a, b, c) = (created.Items[0], created.Items[1], created.Items[2]);

        var edit = Request("Checklist v2", Upload("C renamed", id: c.Id), Form("New field", FieldType.YesNo), Upload("A", id: a.Id));
        var response = await client.PutAsJsonAsync($"/api/templates/{created.Id}", edit);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync(response);
        updated.Name.Should().Be("Checklist v2");
        updated.Items.Select(i => i.Name).Should().Equal("C renamed", "New field", "A");
        updated.Items[0].Id.Should().Be(c.Id);
        updated.Items[2].Id.Should().Be(a.Id);
        updated.Items.Should().NotContain(i => i.Id == b.Id);
    }

    [Fact]
    public async Task Editing_with_an_item_from_another_template_is_rejected()
    {
        var client = await OfficerAsync();
        var one = await ReadAsync(await client.PostAsJsonAsync("/api/templates", Request("One", Upload("A"))));
        var two = await ReadAsync(await client.PostAsJsonAsync("/api/templates", Request("Two", Upload("B"))));

        var response = await client.PutAsJsonAsync(
            $"/api/templates/{one.Id}", Request("One", Upload("B", id: two.Items[0].Id)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Archived_templates_are_hidden_by_default_and_cannot_be_edited_until_restored()
    {
        var client = await OfficerAsync();
        var created = await ReadAsync(await client.PostAsJsonAsync("/api/templates", Request("Old", Upload("A"))));

        var archived = await ReadAsync(await client.PostAsync($"/api/templates/{created.Id}/archive", null));

        archived.IsArchived.Should().BeTrue();
        archived.ArchivedAt.Should().NotBeNull();
        (await ListAsync(client, ""))!.Items
            .Should().NotContain(t => t.Id == created.Id);
        (await ListAsync(client, "includeArchived=true"))!.Items
            .Should().Contain(t => t.Id == created.Id && t.IsArchived);
        (await client.PutAsJsonAsync($"/api/templates/{created.Id}", Request("Old", Upload("A", id: created.Items[0].Id))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var restored = await ReadAsync(await client.PostAsync($"/api/templates/{created.Id}/restore", null));
        restored.IsArchived.Should().BeFalse();
        (await client.PutAsJsonAsync($"/api/templates/{created.Id}", Request("Old", Upload("A", id: created.Items[0].Id))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_templates_return_not_found()
    {
        var client = await OfficerAsync();
        var id = Guid.NewGuid();

        (await client.GetAsync($"/api/templates/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/api/templates/{id}", Request("X"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/templates/{id}/archive", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_form_field_without_a_field_type_is_a_validation_error()
    {
        var client = await OfficerAsync();
        var request = Request("Bad", new TemplateItemRequest(null, "Revenue", ItemKind.FormField, "", false, null, true));

        var response = await client.PostAsJsonAsync("/api/templates", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("items[0].fieldType").And.Contain("requires a field type");
    }

    [Fact]
    public async Task Viewers_can_read_but_not_change_templates()
    {
        var viewer = await ClientForAsync("viewer@example.com", [StaffRoles.Viewer]);
        var id = TemplateSeeder.SbaTemplateId;

        (await viewer.GetAsync("/api/templates")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/templates/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync("/api/templates", Request("X"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PutAsJsonAsync($"/api/templates/{id}", Request("X"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/templates/{id}/archive", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Auditors_and_anonymous_callers_cannot_use_the_template_api()
    {
        var auditor = await ClientForAsync("aud@example.com", [StaffRoles.Auditor]);

        (await auditor.GetAsync("/api/templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().GetAsync("/api/templates")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Template_changes_are_audited_with_ids_and_counts_only()
    {
        var client = await OfficerAsync();
        var created = await ReadAsync(await client.PostAsJsonAsync("/api/templates",
            Request("Secret borrower name", Upload("A"), Upload("B"))));
        await client.PutAsJsonAsync($"/api/templates/{created.Id}",
            Request("Renamed secret", Upload("A", id: created.Items[0].Id)));
        await client.PostAsync($"/api/templates/{created.Id}/archive", null);
        await client.PostAsync($"/api/templates/{created.Id}/restore", null);

        var events = (await AuditEventsAsync()).Where(e => e.TargetType == "template").ToList();

        events.Select(e => e.Action).Should().Equal(
            "template.created", "template.updated", "template.archived", "template.restored");
        events.Should().OnlyContain(e => e.TargetId == created.Id && e.ActorType == DocumentInvite.Api.Common.ActorType.User);
        events[0].DetailsJson.Should().Be("{\"itemCount\":2}");
        events[1].DetailsJson.Should().Be("{\"itemCount\":1,\"itemsAdded\":0,\"itemsRemoved\":1}");
        events.Should().NotContain(e => e.DetailsJson.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failed_validation_writes_no_audit_event()
    {
        var client = await OfficerAsync();

        await client.PostAsJsonAsync("/api/templates", new SaveTemplateRequest("", null, []));

        (await AuditEventsAsync()).Should().NotContain(e => e.TargetType == "template");
    }
}

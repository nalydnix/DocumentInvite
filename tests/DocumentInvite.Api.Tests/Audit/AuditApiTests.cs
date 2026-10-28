using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Settings;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace DocumentInvite.Api.Tests.Audit;

public class AuditApiTests : StaffTestBase
{
    private static readonly string[] AuditUrls =
        ["/api/audit", "/api/audit/export.csv"];

    private async Task ExecuteAsync(string commandText)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    // ---- authorisation ----

    [Theory]
    [InlineData(StaffRoles.Admin)]
    [InlineData(StaffRoles.Auditor)]
    public async Task Admin_and_auditor_can_use_the_audit_endpoints(string role)
    {
        var client = await ClientForAsync("a@example.com", [role]);

        foreach (var url in AuditUrls)
        {
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);
        }

        (await client.PostAsync("/api/audit/verify", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(StaffRoles.LoanOfficer)]
    public async Task Other_roles_are_forbidden(string role)
    {
        var client = await ClientForAsync("o@example.com", [role]);

        foreach (var url in AuditUrls)
        {
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        }

        (await client.PostAsync("/api/audit/verify", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var client = NewClient();

        foreach (var url in AuditUrls)
        {
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
        }

        (await client.PostAsync("/api/audit/verify", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auditor_whose_access_has_expired_is_rejected()
    {
        var client = await ClientForAsync("aud@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddDays(1));
        (await client.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.OK);

        await SetAccessExpiryDirectlyAsync("aud@example.com", DateTimeOffset.UtcNow.AddMinutes(-1));

        foreach (var url in AuditUrls)
        {
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
        }

        (await client.PostAsync("/api/audit/verify", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- search ----

    [Fact]
    public async Task List_is_newest_first_and_pages()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        for (var i = 0; i < 4; i++)
        {
            await admin.PostAsync("/api/audit/verify", null);
        }

        var page1 = await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>("/api/audit?pageSize=2&page=1");
        var page2 = await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>("/api/audit?pageSize=2&page=2");

        page1!.Total.Should().BeGreaterThanOrEqualTo(4);
        page1.Items.Should().HaveCount(2);
        page1.Items[0].Id.Should().BeGreaterThan(page1.Items[1].Id);
        page2!.Items[0].Id.Should().BeLessThan(page1.Items[1].Id);
    }

    [Fact]
    public async Task List_filters_by_action_actor_target_and_date()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        await admin.PostAsync("/api/audit/verify", null);
        var contact = await (await admin.PostAsJsonAsync("/api/contacts",
            new { firstName = "Ann", lastName = "Lee", email = "ann@example.com", partyType = "Borrower" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var contactId = contact.GetProperty("id").GetGuid();

        var byAction = await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>("/api/audit?action=audit.");
        byAction!.Items.Should().NotBeEmpty().And.OnlyContain(x => x.Action.StartsWith("audit."));

        var byTarget = await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/audit?targetType=contact&targetId={contactId}");
        byTarget!.Items.Should().ContainSingle().Which.Action.Should().Be("contact.created");

        var actorId = byTarget.Items[0].ActorId;
        var byActor = await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/audit?actorId={actorId}&action=contact");
        byActor!.Items.Should().ContainSingle();

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        (await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/audit?from={future}"))!.Items.Should().BeEmpty();
        (await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/audit?to={future}"))!.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task List_treats_like_wildcards_in_the_action_filter_literally()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var page = await admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>("/api/audit?action=%25");

        page!.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task List_rejects_invalid_paging(string query)
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        (await admin.GetAsync("/api/audit?" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_rejects_a_date_range_that_ends_before_it_starts()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.GetAsync("/api/audit?from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- export ----

    [Fact]
    public async Task Export_returns_the_filtered_rows_as_csv_and_is_audited()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        await admin.PostAsync("/api/audit/verify", null);
        await admin.PostAsync("/api/audit/verify", null);

        var response = await admin.GetAsync("/api/audit/export.csv?action=audit.verified");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var lines = (await response.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("id,occurredAt,actorType,actorId,action");
        lines.Skip(1).Should().HaveCount(2).And.OnlyContain(l => l.Contains("audit.verified"));

        var events = await AuditEventsAsync();
        var export = events.Single(e => e.Action == "audit.exported");
        export.TargetType.Should().Be("audit");
        export.DetailsJson.Should().Be("{\"rowCount\":2}");
        events.Max(e => e.Id).Should().Be(export.Id);
    }

    [Fact]
    public async Task Export_of_an_empty_filter_has_only_the_header_and_is_audited()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.GetAsync("/api/audit/export.csv?action=nothing.matches");

        (await response.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Should().HaveCount(1);
        (await AuditEventsAsync()).Should().Contain(e => e.Action == "audit.exported" && e.DetailsJson == "{\"rowCount\":0}");
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData(null, "")]
    public void Csv_fields_are_quoted_and_formulas_defused(string? input, string expected) =>
        AuditQueryService.CsvField(input).Should().Be(expected);

    // ---- verification ----

    [Fact]
    public async Task Verify_reports_valid_and_is_audited()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PostAsync("/api/audit/verify", null);

        var result = await response.Content.ReadFromJsonAsync<AuditVerifyResponse>();
        result!.IsValid.Should().BeTrue();
        result.FirstBrokenEventId.Should().BeNull();
        var verified = (await AuditEventsAsync()).Single(e => e.Action == "audit.verified");
        verified.DetailsJson.Should().Contain("\"outcome\":\"Valid\"");
    }

    [Fact]
    public async Task Verify_reports_the_first_broken_event_after_tampering()
    {
        var auditor = await ClientForAsync("aud@example.com", [StaffRoles.Auditor]);
        await auditor.PostAsync("/api/audit/verify", null);
        var events = await AuditEventsAsync();
        events.Should().HaveCountGreaterThan(1);
        var target = events[1];

        await ExecuteAsync($"UPDATE AuditEvents SET Action = N'item.rejected' WHERE Id = {target.Id}");
        var result = await (await auditor.PostAsync("/api/audit/verify", null))
            .Content.ReadFromJsonAsync<AuditVerifyResponse>();

        result!.IsValid.Should().BeFalse();
        result.FirstBrokenEventId.Should().Be(target.Id);
        (await AuditEventsAsync()).Last().DetailsJson.Should().Contain("\"outcome\":\"Broken\"");
    }

    // ---- settings ----

    [Fact]
    public async Task Admin_can_read_and_update_settings_and_the_change_is_audited()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        (await admin.GetFromJsonAsync<SettingsResponse>("/api/settings"))
            .Should().Be(new SettingsResponse(7, 365));

        var response = await admin.PutAsJsonAsync("/api/settings",
            new { defaultReminderIntervalDays = 3, retentionDaysAfterCompletion = 90 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<SettingsResponse>("/api/settings")).Should().Be(new SettingsResponse(3, 90));
        var updated = (await AuditEventsAsync()).Single(e => e.Action == "settings.updated");
        updated.TargetType.Should().Be("settings");
        updated.DetailsJson.Should().Be("{\"defaultReminderIntervalDays\":3,\"retentionDaysAfterCompletion\":90}");
    }

    [Fact]
    public async Task Unchanged_settings_are_not_audited()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PutAsJsonAsync("/api/settings",
            new { defaultReminderIntervalDays = 7, retentionDaysAfterCompletion = 365 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditEventsAsync()).Should().NotContain(e => e.Action == "settings.updated");
    }

    [Theory]
    [InlineData(0, 365)]
    [InlineData(91, 365)]
    [InlineData(7, 29)]
    [InlineData(7, 3651)]
    public async Task Settings_out_of_range_are_rejected(int reminder, int retention)
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PutAsJsonAsync("/api/settings",
            new { defaultReminderIntervalDays = reminder, retentionDaysAfterCompletion = retention });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_are_admin_only()
    {
        foreach (var (email, role) in new[]
                 {
                     ("lo@example.com", StaffRoles.LoanOfficer),
                     ("aud@example.com", StaffRoles.Auditor),
                 })
        {
            var client = await ClientForAsync(email, [role]);
            (await client.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await client.PutAsJsonAsync("/api/settings",
                    new { defaultReminderIntervalDays = 3, retentionDaysAfterCompletion = 90 }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }

        (await NewClient().GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

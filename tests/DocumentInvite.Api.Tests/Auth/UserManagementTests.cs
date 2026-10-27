using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Users;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace DocumentInvite.Api.Tests.Auth;

public class UserManagementTests : StaffTestBase
{
    private const string NewPassword = "Another-Strong-Pw-1";

    private Task<HttpClient> AdminClientAsync() => ClientForAsync("admin@example.com", [StaffRoles.Admin]);

    private static CreateUserRequest NewUser(
        string email = "new.auditor@example.com", string[]? roles = null, DateTimeOffset? expiry = null) =>
        new(email, "New Person", NewPassword, roles ?? [StaffRoles.Auditor], expiry);

    [Theory]
    [InlineData(StaffRoles.LoanOfficer)]
    [InlineData(StaffRoles.Viewer)]
    [InlineData(StaffRoles.Auditor)]
    public async Task Only_admins_can_use_the_user_api(string role)
    {
        var client = await ClientForAsync("other@example.com", [role]);

        (await client.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/users", NewUser())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admins_list_users_with_their_roles_and_expiry()
    {
        var admin = await AdminClientAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        await CreateUserAsync("aud@example.com", [StaffRoles.Auditor], expiry);

        var users = await admin.GetFromJsonAsync<PagedResponse<UserResponse>>("/api/users");

        users!.Items.Should().HaveCount(2);
        users.Total.Should().Be(2);
        var auditor = users.Items.Single(u => u.Email == "aud@example.com");
        auditor.Roles.Should().BeEquivalentTo(StaffRoles.Auditor);
        auditor.AccessExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
        auditor.IsLockedOut.Should().BeFalse();
    }


    private async Task CreateManyAsync(HttpClient admin, int count)
    {
        for (var i = 0; i < count; i++)
        {
            (await admin.PostAsJsonAsync("/api/users", NewUser($"user{i:00}@example.com", [StaffRoles.Viewer])))
                .EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task The_user_list_defaults_to_page_one_of_25_in_email_order()
    {
        var admin = await AdminClientAsync();
        await CreateManyAsync(admin, 29); // plus the admin: 30 users

        var body = (await admin.GetFromJsonAsync<PagedResponse<UserResponse>>("/api/users"))!;

        body.Page.Should().Be(1);
        body.PageSize.Should().Be(25);
        body.Total.Should().Be(30);
        body.Items.Should().HaveCount(25);
        body.Items.Select(u => u.Email).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        body.Items.Should().OnlyContain(u => u.Roles.Count > 0);
    }

    [Fact]
    public async Task The_last_partial_page_holds_the_remaining_users_and_pages_do_not_overlap()
    {
        var admin = await AdminClientAsync();
        await CreateManyAsync(admin, 6); // plus the admin: 7 users

        var seen = new List<Guid>();
        foreach (var page in new[] { 1, 2, 3 })
        {
            var body = (await admin.GetFromJsonAsync<PagedResponse<UserResponse>>($"/api/users?page={page}&pageSize=3"))!;
            body.Total.Should().Be(7);
            body.Items.Should().HaveCount(page == 3 ? 1 : 3);
            body.Items.Should().OnlyContain(u => u.Roles.Count > 0);
            seen.AddRange(body.Items.Select(u => u.Id));
        }

        seen.Should().HaveCount(7).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_user_page_past_the_end_is_empty_but_reports_the_total()
    {
        var admin = await AdminClientAsync();

        var body = (await admin.GetFromJsonAsync<PagedResponse<UserResponse>>("/api/users?page=9&pageSize=5"))!;

        body.Items.Should().BeEmpty();
        body.Total.Should().Be(1);
        body.Page.Should().Be(9);
        body.PageSize.Should().Be(5);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=abc")]
    public async Task Invalid_user_paging_returns_a_validation_problem(string query)
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/users?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Admins_create_a_user_who_can_then_sign_in()
    {
        var admin = await AdminClientAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(30);

        var response = await admin.PostAsJsonAsync("/api/users", NewUser(expiry: expiry));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<UserResponse>();
        created!.Roles.Should().BeEquivalentTo(StaffRoles.Auditor);
        response.Headers.Location!.ToString().Should().EndWith($"/api/users/{created.Id}");

        (await PostLoginAsync("new.auditor@example.com", NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creating_a_user_with_a_weak_password_is_a_validation_error()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/users", NewUser() with { Password = "Short-Pw-1!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("password");
    }

    [Fact]
    public async Task Creating_a_duplicate_user_is_a_validation_error()
    {
        var admin = await AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/users", NewUser())).EnsureSuccessStatusCode();

        var response = await admin.PostAsJsonAsync("/api/users", NewUser());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("not-an-email", new[] { "Viewer" })]
    [InlineData("ok@example.com", new[] { "Wizard" })]
    [InlineData("ok@example.com", new string[0])]
    [InlineData("ok@example.com", new[] { "Viewer", "Viewer" })]
    public async Task Creating_a_user_validates_the_email_and_roles(string email, string[] roles)
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/users", NewUser(email, roles));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_expiry_date_can_only_be_set_for_an_auditor()
    {
        var admin = await AdminClientAsync();

        var onCreate = await admin.PostAsJsonAsync(
            "/api/users", NewUser("viewer@example.com", [StaffRoles.Viewer], DateTimeOffset.UtcNow.AddDays(1)));
        onCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var viewer = await CreateUserAsync("v2@example.com", [StaffRoles.Viewer]);
        var onUpdate = await admin.PutAsJsonAsync(
            $"/api/users/{viewer.Id}/access-expiry", new SetAccessExpiryRequest(DateTimeOffset.UtcNow.AddDays(1)));
        onUpdate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admins_set_and_clear_an_auditors_expiry_and_it_takes_effect()
    {
        var admin = await AdminClientAsync();
        var auditor = await CreateUserAsync("aud@example.com", [StaffRoles.Auditor]);

        var past = await admin.PutAsJsonAsync(
            $"/api/users/{auditor.Id}/access-expiry", new SetAccessExpiryRequest(DateTimeOffset.UtcNow.AddMinutes(-5)));
        past.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostLoginAsync("aud@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var cleared = await admin.PutAsJsonAsync($"/api/users/{auditor.Id}/access-expiry", new SetAccessExpiryRequest(null));
        (await cleared.Content.ReadFromJsonAsync<UserResponse>())!.AccessExpiresAt.Should().BeNull();
        (await PostLoginAsync("aud@example.com", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admins_change_roles_and_removing_auditor_clears_the_expiry()
    {
        var admin = await AdminClientAsync();
        var auditor = await CreateUserAsync("aud@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddDays(5));

        var response = await admin.PutAsJsonAsync(
            $"/api/users/{auditor.Id}/roles", new SetRolesRequest([StaffRoles.Viewer, StaffRoles.LoanOfficer]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<UserResponse>();
        updated!.Roles.Should().BeEquivalentTo(StaffRoles.Viewer, StaffRoles.LoanOfficer);
        updated.AccessExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_role()
    {
        var admin = await AdminClientAsync();
        var me = (await admin.GetFromJsonAsync<MeResponse>("/api/me"))!;

        var response = await admin.PutAsJsonAsync($"/api/users/{me.Id}/roles", new SetRolesRequest([StaffRoles.Viewer]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_users_return_404()
    {
        var admin = await AdminClientAsync();

        (await admin.GetAsync($"/api/users/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}/roles", new SetRolesRequest([StaffRoles.Viewer])))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}/access-expiry", new SetAccessExpiryRequest(null)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task User_creation_role_changes_and_expiry_changes_are_audited_with_ids_only()
    {
        var admin = await AdminClientAsync();
        var created = await (await admin.PostAsJsonAsync("/api/users", NewUser(expiry: DateTimeOffset.UtcNow.AddDays(3))))
            .Content.ReadFromJsonAsync<UserResponse>();
        await admin.PutAsJsonAsync($"/api/users/{created!.Id}/roles", new SetRolesRequest([StaffRoles.Auditor, StaffRoles.Viewer]));
        await admin.PutAsJsonAsync($"/api/users/{created.Id}/access-expiry", new SetAccessExpiryRequest(null));

        var events = (await AuditEventsAsync()).Where(e => e.TargetId == created.Id).ToList();

        events.Select(e => e.Action).Should().Equal(
            "user.created", "user.roles_changed", "user.access_expiry_changed");
        events.Should().OnlyContain(e => e.ActorId != null && e.TargetType == "user");
        events.Should().OnlyContain(e =>
            !e.DetailsJson.Contains("example.com") && !e.DetailsJson.Contains("New Person"));
        events[1].DetailsJson.Should().Contain("\"roleViewer\":\"Added\"");
    }

    [Fact]
    public async Task The_first_admin_is_created_from_configuration_once()
    {
        void Configure(IWebHostBuilder builder)
        {
            builder.UseSetting("Bootstrap:AdminEmail", "root@example.com");
            builder.UseSetting("Bootstrap:AdminPassword", "Bootstrap-Pass-12");
        }

        using (var first = Factory.WithWebHostBuilder(Configure))
        {
            first.CreateClient();
        }

        (await PostLoginAsync("root@example.com", "Bootstrap-Pass-12")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var second = Factory.WithWebHostBuilder(Configure))
        {
            second.CreateClient();
        }

        var admin = await AdminClientAsyncFor("root@example.com", "Bootstrap-Pass-12");
        var users = await admin.GetFromJsonAsync<PagedResponse<UserResponse>>("/api/users");
        users!.Items.Should().ContainSingle();
        users.Items[0].Roles.Should().BeEquivalentTo(StaffRoles.Admin);
    }

    private async Task<HttpClient> AdminClientAsyncFor(string email, string password)
    {
        var token = await SignInAsync(email, password);
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}

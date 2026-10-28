using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Users;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Auth;

public class UserManagementTests : StaffTestBase
{
    private const string NewPassword = "Another-Strong-Pw-1";

    private Task<HttpClient> AdminClientAsync() => ClientForAsync("admin@example.com", [StaffRoles.Admin]);

    private static CreateUserRequest NewUser(
        string email = "new.auditor@example.com", string? role = StaffRoles.Auditor, DateTimeOffset? expiry = null) =>
        new(email, "New Person", NewPassword, role, expiry);

    [Theory]
    [InlineData(StaffRoles.LoanOfficer)]
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
        auditor.Role.Should().Be(StaffRoles.Auditor);
        auditor.AccessExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
        auditor.IsLockedOut.Should().BeFalse();
    }


    private async Task CreateManyAsync(HttpClient admin, int count)
    {
        for (var i = 0; i < count; i++)
        {
            (await admin.PostAsJsonAsync("/api/users", NewUser($"user{i:00}@example.com", StaffRoles.LoanOfficer)))
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
        body.Items.Should().OnlyContain(u => u.Role != "");
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
            body.Items.Should().OnlyContain(u => u.Role != "");
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
        created!.Role.Should().Be(StaffRoles.Auditor);
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
    [InlineData("not-an-email", "LoanOfficer")]
    [InlineData("ok@example.com", "Wizard")]
    [InlineData("ok@example.com", "")]
    [InlineData("ok@example.com", null)]
    [InlineData("ok@example.com", "LoanOfficer,Auditor")]
    [InlineData("ok@example.com", "Viewer")]
    public async Task Creating_a_user_validates_the_email_and_role(string email, string? role)
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/users", NewUser(email, role));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_expiry_date_can_only_be_set_for_an_auditor()
    {
        var admin = await AdminClientAsync();

        var onCreate = await admin.PostAsJsonAsync(
            "/api/users", NewUser("officer@example.com", StaffRoles.LoanOfficer, DateTimeOffset.UtcNow.AddDays(1)));
        onCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var officer = await CreateUserAsync("v2@example.com", [StaffRoles.LoanOfficer]);
        var onUpdate = await admin.PutAsJsonAsync(
            $"/api/users/{officer.Id}/access-expiry", new SetAccessExpiryRequest(DateTimeOffset.UtcNow.AddDays(1)));
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
    public async Task Admins_change_the_role_and_leaving_auditor_clears_the_expiry()
    {
        var admin = await AdminClientAsync();
        var auditor = await CreateUserAsync("aud@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddDays(5));

        var response = await admin.PutAsJsonAsync(
            $"/api/users/{auditor.Id}/role", new SetRoleRequest(StaffRoles.LoanOfficer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<UserResponse>();
        updated!.Role.Should().Be(StaffRoles.LoanOfficer);
        updated.AccessExpiresAt.Should().BeNull();
        (await AuditEventsAsync()).Select(e => e.Action).Should().Contain("user.access_expiry_changed");
        var after = await admin.GetFromJsonAsync<UserResponse>($"/api/users/{auditor.Id}");
        after!.Role.Should().Be(StaffRoles.LoanOfficer);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Wizard")]
    public async Task Setting_a_missing_or_unknown_role_is_a_validation_problem(string? role)
    {
        var admin = await AdminClientAsync();
        var user = await CreateUserAsync("v3@example.com", [StaffRoles.LoanOfficer]);

        var response = await admin.PutAsJsonAsync($"/api/users/{user.Id}/role", new SetRoleRequest(role));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Several_roles_in_a_request_are_rejected()
    {
        var admin = await AdminClientAsync();
        var user = await CreateUserAsync("v4@example.com", [StaffRoles.LoanOfficer]);

        var response = await admin.PutAsJsonAsync($"/api/users/{user.Id}/role", new { roles = new[] { "LoanOfficer", "Auditor" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_migration_reduces_multi_role_users_to_their_highest_role()
    {
        var admin = await AdminClientAsync();
        var both = await CreateUserAsync("m1@example.com", [StaffRoles.Auditor, StaffRoles.LoanOfficer], DateTimeOffset.UtcNow.AddDays(5));
        var top = await CreateUserAsync("m3@example.com", [StaffRoles.Admin, StaffRoles.LoanOfficer]);
        var single = await CreateUserAsync("m4@example.com", [StaffRoles.Auditor]);

        // The list shows the highest role even before the data is migrated.
        var before = (await admin.GetFromJsonAsync<PagedResponse<UserResponse>>("/api/users"))!;
        before.Items.Single(u => u.Id == top.Id).Role.Should().Be(StaffRoles.Admin);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(SingleUserRole.KeepHighestRoleSql);
            await db.Database.ExecuteSqlRawAsync(SingleUserRole.ClearNonAuditorExpirySql);
        }

        using var check = Factory.Services.CreateScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        async Task<string[]> RolesOf(Guid id) => (await users.GetRolesAsync((await users.FindByIdAsync(id.ToString()))!)).ToArray();
        (await RolesOf(both.Id)).Should().Equal(StaffRoles.LoanOfficer);
        (await RolesOf(top.Id)).Should().Equal(StaffRoles.Admin);
        (await RolesOf(single.Id)).Should().Equal(StaffRoles.Auditor);
        (await users.FindByIdAsync(both.Id.ToString()))!.AccessExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task The_migration_moves_viewer_users_to_loan_officer_and_leaves_other_users_alone()
    {
        var viewerRoleId = new Guid("A1000000-0000-0000-0000-000000000003");
        var viewer = await CreateUserAsync("rv1@example.com", [StaffRoles.Auditor]);
        var other = await CreateUserAsync("rv2@example.com", [StaffRoles.Auditor]);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The Viewer role no longer exists in the model, so recreate it as an upgraded database would have it.
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp) VALUES ({viewerRoleId}, 'Viewer', 'VIEWER', 'x')");
            await db.Database.ExecuteSqlAsync(
                $"UPDATE AspNetUserRoles SET RoleId = {viewerRoleId} WHERE UserId = {viewer.Id}");
            await db.Database.ExecuteSqlRawAsync(RemoveViewerRole.ReassignViewersSql);
            await db.Database.ExecuteSqlAsync($"DELETE FROM AspNetRoles WHERE Id = {viewerRoleId}");
        }

        using var check = Factory.Services.CreateScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        async Task<string[]> RolesOf(Guid id) => (await users.GetRolesAsync((await users.FindByIdAsync(id.ToString()))!)).ToArray();
        (await RolesOf(viewer.Id)).Should().Equal(StaffRoles.LoanOfficer);
        (await RolesOf(other.Id)).Should().Equal(StaffRoles.Auditor);
    }

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_role()
    {
        var admin = await AdminClientAsync();
        var me = (await admin.GetFromJsonAsync<MeResponse>("/api/me"))!;

        var response = await admin.PutAsJsonAsync($"/api/users/{me.Id}/role", new SetRoleRequest(StaffRoles.LoanOfficer));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_users_return_404()
    {
        var admin = await AdminClientAsync();

        (await admin.GetAsync($"/api/users/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}/role", new SetRoleRequest(StaffRoles.LoanOfficer)))
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
        await admin.PutAsJsonAsync($"/api/users/{created!.Id}/role", new SetRoleRequest(StaffRoles.LoanOfficer));

        var events = (await AuditEventsAsync()).Where(e => e.TargetId == created.Id).ToList();

        events.Select(e => e.Action).Should().Equal(
            "user.created", "user.roles_changed", "user.access_expiry_changed");
        events.Should().OnlyContain(e => e.ActorId != null && e.TargetType == "user");
        events.Should().OnlyContain(e =>
            !e.DetailsJson.Contains("example.com") && !e.DetailsJson.Contains("New Person"));
        events[1].DetailsJson.Should().Contain("\"roleLoanOfficer\":\"Added\"");
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
        users.Items[0].Role.Should().Be(StaffRoles.Admin);
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

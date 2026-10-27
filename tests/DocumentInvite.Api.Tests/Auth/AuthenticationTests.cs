using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Auth;

public class AuthenticationTests(SqlServerFixture sql) : StaffTestBase(sql)
{
    private sealed record MeBody(Guid Id, string Email, string DisplayName, string[] Roles, DateTimeOffset? AccessExpiresAt);

    [Fact]
    public async Task The_four_staff_roles_are_seeded()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var names = await db.Roles.Select(r => r.Name).ToListAsync();

        names.Should().BeEquivalentTo("Admin", "LoanOfficer", "Viewer", "Auditor");
    }

    [Theory]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/forgotPassword")]
    [InlineData("/api/auth/resetPassword")]
    [InlineData("/api/auth/confirmEmail")]
    [InlineData("/api/auth/resendConfirmationEmail")]
    public async Task Public_registration_and_recovery_endpoints_are_disabled(string path)
    {
        var response = await NewClient().PostAsJsonAsync(path, new { email = "new@example.com", password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sign_in_returns_bearer_tokens_and_me_returns_the_user_with_roles()
    {
        await CreateUserAsync("officer@example.com", [StaffRoles.LoanOfficer, StaffRoles.Viewer]);

        var token = await SignInAsync("officer@example.com");
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<MeBody>("/api/me");

        me!.Email.Should().Be("officer@example.com");
        me.Roles.Should().BeEquivalentTo(StaffRoles.LoanOfficer, StaffRoles.Viewer);
        token.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401_not_a_redirect()
    {
        var response = await NewClient().GetAsync("/api/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_refresh_token_issues_a_new_access_token()
    {
        await CreateUserAsync("viewer@example.com", [StaffRoles.Viewer]);
        var token = await SignInAsync("viewer@example.com");

        var response = await NewClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = token.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var renewed = await response.Content.ReadFromJsonAsync<TokenResponse>();
        renewed!.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Passwords_shorter_than_12_characters_are_rejected()
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();

        var result = await users.CreateAsync(
            new AppUser { UserName = "short@example.com", Email = "short@example.com", DisplayName = "Short" }, "Short-Pw-1!");

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "PasswordTooShort");
    }

    [Fact]
    public async Task An_account_locks_after_five_failed_sign_ins_even_for_the_correct_password()
    {
        await CreateUserAsync("locked@example.com", [StaffRoles.Admin]);
        var client = NewClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await PostLoginAsync("locked@example.com", "Wrong-Password-1", client)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        var response = await PostLoginAsync("locked@example.com", Password, client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("LockedOut");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lockoutEnd = await db.Users.Where(x => x.Email == "locked@example.com").Select(x => x.LockoutEnd).SingleAsync();
        lockoutEnd.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Sign_in_success_failure_and_lockout_are_audited_with_ids_only()
    {
        var user = await CreateUserAsync("audited@example.com", [StaffRoles.Admin]);
        var client = NewClient();

        await SignInAsync("audited@example.com");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await PostLoginAsync("audited@example.com", "Wrong-Password-1", client);
        }

        await PostLoginAsync("nobody@example.com", "Wrong-Password-1", client);

        var events = await AuditEventsAsync();
        events.Count(e => e.Action == "auth.signin_succeeded" && e.TargetId == user.Id).Should().Be(1);
        events.Count(e => e.Action == "auth.signin_failed" && e.TargetId == user.Id).Should().Be(5);
        events.Count(e => e.Action == "auth.locked_out" && e.TargetId == user.Id).Should().Be(1);
        events.Count(e => e.Action == "auth.signin_failed" && e.TargetId == null).Should().Be(1);
        events.Should().OnlyContain(e =>
            !e.DetailsJson.Contains("example.com") && !e.DetailsJson.Contains("Password"));
    }
}

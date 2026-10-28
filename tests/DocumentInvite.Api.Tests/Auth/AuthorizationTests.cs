using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;

namespace DocumentInvite.Api.Tests.Auth;

public class AuthorizationTests : StaffTestBase
{
    // Role -> the probe endpoints that role may call (architecture section 5).
    public static TheoryData<string, string, bool> Matrix()
    {
        var allowed = new Dictionary<string, string[]>
        {
            [StaffRoles.Admin] = ["admin", "staff", "read", "audit", "any"],
            [StaffRoles.LoanOfficer] = ["staff", "read", "any"],
            [StaffRoles.Auditor] = ["audit", "any"],
        };

        var data = new TheoryData<string, string, bool>();
        foreach (var role in StaffRoles.All)
        {
            foreach (var endpoint in new[] { "admin", "staff", "read", "audit", "any" })
            {
                data.Add(role, endpoint, allowed[role].Contains(endpoint));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Each_role_reaches_only_the_endpoints_its_policies_allow(string role, string endpoint, bool allowed)
    {
        var client = await ClientForAsync($"{role.ToLowerInvariant()}@example.com", [role]);

        var response = await client.GetAsync($"/test/policy/{endpoint}");

        response.StatusCode.Should().Be(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("staff")]
    [InlineData("read")]
    [InlineData("audit")]
    public async Task Anonymous_callers_are_rejected_by_every_policy(string endpoint)
    {
        var response = await NewClient().GetAsync($"/test/policy/{endpoint}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_account_whose_expiry_has_passed_cannot_sign_in()
    {
        await CreateUserAsync("expired@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await PostLoginAsync("expired@example.com", Password);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuditEventsAsync()).Should().Contain(e =>
            e.Action == "auth.signin_failed" && e.DetailsJson.Contains("AccessExpired"));
    }

    [Fact]
    public async Task An_account_with_a_future_expiry_can_sign_in()
    {
        await CreateUserAsync("later@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddDays(30));

        var response = await PostLoginAsync("later@example.com", Password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_stops_working_the_moment_the_account_expires()
    {
        var client = await ClientForAsync("soon@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddDays(1));
        (await client.GetAsync("/test/policy/audit")).StatusCode.Should().Be(HttpStatusCode.OK);

        await SetAccessExpiryDirectlyAsync("soon@example.com", DateTimeOffset.UtcNow.AddSeconds(-1));

        (await client.GetAsync("/test/policy/audit")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/test/policy/any")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_expired_account_cannot_renew_its_tokens()
    {
        await CreateUserAsync("renew@example.com", [StaffRoles.Auditor], DateTimeOffset.UtcNow.AddDays(1));
        var token = await SignInAsync("renew@example.com");
        await SetAccessExpiryDirectlyAsync("renew@example.com", DateTimeOffset.UtcNow.AddSeconds(-1));

        var response = await NewClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = token.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_malformed_token_is_rejected()
    {
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-valid-token");

        (await client.GetAsync("/test/policy/any")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

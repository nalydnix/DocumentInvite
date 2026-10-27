using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Infrastructure;

public sealed record TokenResponse(string AccessToken, string RefreshToken);

/// <summary>Helpers for tests that sign staff in through the real HTTP endpoints.</summary>
public abstract class StaffTestBase(SqlServerFixture sql) : IntegrationTestBase(sql)
{
    protected const string Password = "Correct-Horse-12";

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(PolicyProbeController).Assembly);
    }

    protected HttpClient NewClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected async Task<AppUser> CreateUserAsync(
        string email, string[] roles, DateTimeOffset? accessExpiresAt = null, string password = Password)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Test " + email,
            AccessExpiresAt = accessExpiresAt,
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
        return user;
    }

    protected async Task<HttpResponseMessage> PostLoginAsync(string email, string password, HttpClient? client = null) =>
        await (client ?? NewClient()).PostAsJsonAsync("/api/auth/login", new { email, password });

    protected async Task<TokenResponse> SignInAsync(string email, string password = Password)
    {
        var response = await PostLoginAsync(email, password);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    protected async Task<HttpClient> ClientForAsync(string email, string[] roles, DateTimeOffset? accessExpiresAt = null)
    {
        await CreateUserAsync(email, roles, accessExpiresAt);
        var token = await SignInAsync(email);
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    protected async Task SetAccessExpiryDirectlyAsync(string email, DateTimeOffset? value)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(x => x.Email == email)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AccessExpiresAt, value));
    }

    protected async Task<List<AuditEvent>> AuditEventsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }
}

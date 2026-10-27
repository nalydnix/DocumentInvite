using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests;

public class PlatformTests : PlatformTestBase
{
    [Fact]
    public async Task Migrations_are_applied_and_settings_row_is_seeded()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var settings = await db.Settings.SingleAsync();

        settings.Id.Should().Be(AppSettings.SingletonId);
        settings.RowVersion.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Validation_failure_returns_problem_details_with_field_errors()
    {
        var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/test/echo", new { email = "not-an-email", quantity = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(400);
        var errors = root.GetProperty("errors");
        errors.TryGetProperty("email", out _).Should().BeTrue();
        errors.TryGetProperty("quantity", out _).Should().BeTrue();
        root.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Valid_request_passes_validation()
    {
        var response = await Factory.CreateClient().PostAsJsonAsync("/test/echo", new { email = "a@example.com", quantity = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unhandled_exception_hides_details_outside_development()
    {
        var response = await Factory.CreateClient().GetAsync("/test/boom");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("secret internal failure").And.NotContain("InvalidOperationException").And.NotContain(" at ");
    }

    [Fact]
    public async Task Unhandled_exception_includes_details_in_development()
    {
        var response = await WithEnvironment("Development").CreateClient().GetAsync("/test/boom");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("secret internal failure");
    }

    [Fact]
    public async Task Not_found_returns_problem_details()
    {
        var response = await Factory.CreateClient().GetAsync("/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Correlation_id_is_echoed_when_supplied()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await Factory.CreateClient().SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Which.Should().Be("abc-123");
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing_or_invalid()
    {
        var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad value with spaces");

        var generated = await client.GetAsync("/health");
        var invalid = await client.SendAsync(request);

        generated.Headers.GetValues("X-Correlation-Id").Single().Should().NotBeNullOrWhiteSpace();
        invalid.Headers.GetValues("X-Correlation-Id").Single().Should().NotBe("bad value with spaces");
    }
}

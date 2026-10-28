using System.Net;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocumentInvite.Api.Tests;

public class ProductionSecurityTests : IntegrationTestBase
{
    private HttpClient SecureClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.example.test") });

    [Fact]
    public async Task Responses_carry_security_headers_and_hsts_over_https()
    {
        var response = await SecureClient(Factory).GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle("no-referrer");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.GetValues("Strict-Transport-Security").Single().Should().Contain("max-age=31536000");
    }

    [Fact]
    public async Task Cors_allows_only_configured_origins()
    {
        var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Cors:Origins:0", "https://app.example.com"));
        var client = SecureClient(factory);

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/health");
        allowed.Headers.Add("Origin", "https://app.example.com");
        var other = new HttpRequestMessage(HttpMethod.Get, "/health");
        other.Headers.Add("Origin", "https://evil.example.com");

        (await client.SendAsync(allowed)).Headers.GetValues("Access-Control-Allow-Origin")
            .Should().ContainSingle("https://app.example.com");
        (await client.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Cors_is_closed_when_nothing_is_configured()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://app.example.com");

        (await SecureClient(Factory).SendAsync(request)).Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Scalar_can_be_enabled_explicitly_in_production()
    {
        var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Scalar:Enabled", "true"));

        (await factory.CreateClient().GetAsync("/scalar")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Factory.CreateClient().GetAsync("/scalar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

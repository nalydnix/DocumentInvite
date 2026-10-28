using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Infrastructure.Blob;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Tests.Files;

/// <summary>
/// The Azurite CORS setup. Origin resolution runs anywhere; the preflight test runs against Azurite only when
/// <c>TEST_AZURITE_CONNECTION</c> is set.
/// </summary>
public class BlobCorsConfiguratorTests
{
    [Fact]
    public void The_web_origin_comes_from_Portal_BaseUrl_on_any_port_in_development()
    {
        var origins = BlobCorsConfigurator.ResolveOrigins([], "http://localhost:5174/", includeWebBaseUrl: true);

        origins.Should().Equal("http://localhost:5174");
    }

    [Fact]
    public void Portal_BaseUrl_is_ignored_outside_development_and_explicit_origins_are_normalised_and_deduplicated()
    {
        var origins = BlobCorsConfigurator.ResolveOrigins(
            ["https://app.example.com/", "https://APP.example.com", "", "not a url", "*"],
            "http://localhost:5174", includeWebBaseUrl: false);

        origins.Should().Equal("https://app.example.com");
    }

    [Fact]
    public void Nothing_is_configured_when_no_origin_is_known()
    {
        BlobCorsConfigurator.ResolveOrigins([], "", includeWebBaseUrl: true).Should().BeEmpty();
    }

    [Fact]
    public void The_rule_allows_the_upload_methods_and_headers()
    {
        var rule = BlobCorsConfigurator.BuildRule(["http://localhost:5174"]);

        rule.AllowedOrigins.Should().Be("http://localhost:5174");
        rule.AllowedMethods.Should().Be("PUT,GET,HEAD,OPTIONS");
        rule.AllowedHeaders.Should().Be("x-ms-*,content-type");
        rule.ExposedHeaders.Should().Be("x-ms-*,ETag");
    }

    [Fact]
    public async Task Azurite_answers_a_browser_preflight_from_the_configured_web_origin()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_AZURITE_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        const string origin = "http://localhost:5199";
        var configurator = new BlobCorsConfigurator(
            Options.Create(new BlobOptions { ConnectionString = connection }),
            Options.Create(new PortalOptions { BaseUrl = origin + "/" }),
            new DevelopmentEnvironment(),
            NullLogger<BlobCorsConfigurator>.Instance);
        await configurator.StartAsync(CancellationToken.None);

        var account = new Azure.Storage.Blobs.BlobServiceClient(connection);
        using var http = new HttpClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri(account.Uri.AbsoluteUri.TrimEnd('/') + "/documents/invites/x/y"));
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-ms-blob-type,content-type");
        var response = await http.SendAsync(preflight);

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain(origin);
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

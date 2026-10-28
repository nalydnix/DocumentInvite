using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Tests;

public sealed class PortalBaseUrlDefaultTests : IntegrationTestBase
{
    [Fact]
    public void Development_defaults_the_portal_base_url_to_the_vite_dev_origin()
    {
        using var factory = NewFactory("Development", portalBaseUrl: null);

        var options = factory.Services.GetRequiredService<IOptions<PortalOptions>>().Value;

        options.BaseUrl.Should().Be("http://localhost:5173");
        factory.Services.GetRequiredService<IPortalLinkBuilder>().For("t").Should().Be("http://localhost:5173/p/t");
    }

    [Fact]
    public void An_explicit_portal_base_url_overrides_the_development_default()
    {
        using var factory = NewFactory("Development", portalBaseUrl: "http://localhost:5174");

        factory.Services.GetRequiredService<IOptions<PortalOptions>>().Value.BaseUrl.Should().Be("http://localhost:5174");
    }

    [Fact]
    public void Production_has_no_portal_base_url_default_and_fails_clearly()
    {
        using var factory = NewFactory("Production", portalBaseUrl: null);

        factory.Services.GetRequiredService<IOptions<PortalOptions>>().Value.BaseUrl.Should().BeEmpty();
        var act = () => factory.Services.GetRequiredService<IPortalLinkBuilder>().For("t");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Portal:BaseUrl*");
    }

    private WebApplicationFactory<Program> NewFactory(string environment, string? portalBaseUrl) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Default", ConnectionString);
            builder.UseSetting("ConnectionStrings:Migrations", ConnectionString);
            if (portalBaseUrl is not null)
            {
                builder.UseSetting("Portal:BaseUrl", portalBaseUrl);
            }
        });
}

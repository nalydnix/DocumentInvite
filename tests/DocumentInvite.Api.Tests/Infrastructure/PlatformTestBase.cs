using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Infrastructure;

/// <summary>Integration test base that also exposes the test-only <see cref="EchoController"/>.</summary>
public abstract class PlatformTestBase(SqlServerFixture sql) : IntegrationTestBase(sql)
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(EchoController).Assembly);
        services.AddValidatorsFromAssemblyContaining<EchoRequestValidator>();
    }
}

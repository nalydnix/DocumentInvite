using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Infrastructure;

/// <summary>
/// Base class for integration tests. Each test class gets its own migrated database and
/// a <see cref="WebApplicationFactory{TEntryPoint}"/> wired to it.
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    protected string ConnectionString { get; private set; } = string.Empty;

    protected WebApplicationFactory<Program> Factory => _factory!;

    public async Task InitializeAsync()
    {
        ConnectionString = await SqlServerFixture.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", ConnectionString);
            builder.UseSetting("ConnectionStrings:Migrations", ConnectionString);
            builder.ConfigureServices(ConfigureServices);
        });
    }

    /// <summary>Override to register test-only services or controllers.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected WebApplicationFactory<Program> WithEnvironment(string environment) =>
        Factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    public virtual async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (ConnectionString.Length > 0)
        {
            await SqlServerFixture.DropDatabaseAsync(ConnectionString);
        }
    }
}

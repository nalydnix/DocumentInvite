using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace DocumentInvite.Api.Tests.Infrastructure;

/// <summary>
/// Provides a SQL Server instance for integration tests. When <c>TEST_SQL_CONNECTION</c> is set
/// (for example a CI service container or LocalDB) that server is used; otherwise a SQL Server
/// container is started with Testcontainers. Every call to <see cref="CreateDatabaseAsync"/>
/// returns a fresh, migrated database.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "TEST_SQL_CONNECTION";

    private readonly List<string> _databases = [];
    private MsSqlContainer? _container;
    private string _serverConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            _serverConnectionString = external;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        _serverConnectionString = _container.GetConnectionString();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"DocumentInviteTests_{Guid.NewGuid():N}";
        _databases.Add(name);

        await using (var connection = new SqlConnection(_serverConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}]";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = name }.ConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        return connectionString;
    }

    public async Task DisposeAsync()
    {
        if (_databases.Count > 0)
        {
            try
            {
                await using var connection = new SqlConnection(_serverConnectionString);
                await connection.OpenAsync();
                foreach (var name in _databases)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
                    await command.ExecuteNonQueryAsync();
                }
            }
            catch (SqlException)
            {
                // Best-effort cleanup; a container is discarded anyway.
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

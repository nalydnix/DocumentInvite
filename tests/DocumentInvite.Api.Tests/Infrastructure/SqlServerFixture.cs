using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace DocumentInvite.Api.Tests.Infrastructure;

/// <summary>
/// Process-wide SQL Server for integration tests, started lazily and shared by every test class.
/// When <c>TEST_SQL_CONNECTION</c> is set (for example a CI service container or LocalDB) that
/// server is used; otherwise a SQL Server container is started with Testcontainers (removed by
/// its resource reaper when the test process ends).
/// A template database is migrated and backed up once; <see cref="CreateDatabaseAsync"/> restores
/// a private copy from that backup, so no class pays for migrations.
/// </summary>
public static class SqlServerFixture
{
    public const string ConnectionVariable = "TEST_SQL_CONNECTION";

    private static readonly Lazy<Task<ServerState>> State = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task<string> CreateDatabaseAsync()
    {
        var state = await State.Value;
        var name = $"DocumentInviteTests_{Guid.NewGuid():N}";

        var moves = string.Join(", ", state.Files.Select((f, i) =>
            $"MOVE N'{f.LogicalName}' TO N'{state.DataPath}{name}_{i}{(f.IsLog ? ".ldf" : ".mdf")}'"));

        await ExecuteAsync(
            state.ServerConnectionString,
            $"RESTORE DATABASE [{name}] FROM DISK = N'{state.BackupFile}' WITH {moves}");

        return new SqlConnectionStringBuilder(state.ServerConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var state = await State.Value;
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        try
        {
            await ExecuteAsync(
                state.ServerConnectionString,
                $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
        }
        catch (SqlException)
        {
            // Best-effort cleanup; a container is discarded anyway.
        }
    }

    private static async Task<ServerState> StartAsync()
    {
        string serverConnectionString;
        var external = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            serverConnectionString = external;
        }
        else
        {
            var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await container.StartAsync();
            serverConnectionString = container.GetConnectionString();
        }

        var template = $"DocumentInviteTemplate_{Guid.NewGuid():N}";

        string backupPath, dataPath;
        await using (var connection = new SqlConnection(serverConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(512)), " +
                "CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512))";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            backupPath = WithTrailingSeparator(reader.GetString(0));
            dataPath = WithTrailingSeparator(reader.GetString(1));
        }

        var backupFile = $"{backupPath}{template}.bak";

        await ExecuteAsync(serverConnectionString, $"CREATE DATABASE [{template}]");
        try
        {
            var templateConnection = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = template }.ConnectionString;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(templateConnection).Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await ExecuteAsync(serverConnectionString, $"BACKUP DATABASE [{template}] TO DISK = N'{backupFile}' WITH INIT, FORMAT");
        }
        finally
        {
            await ExecuteAsync(
                serverConnectionString,
                $"ALTER DATABASE [{template}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{template}]");
        }

        var files = await ReadFilesAsync(serverConnectionString, backupFile);
        return new ServerState(serverConnectionString, backupFile, dataPath, files);
    }

    private static string WithTrailingSeparator(string path)
    {
        if (path.EndsWith('/') || path.EndsWith('\\'))
        {
            return path;
        }

        return path + (path.Contains('/') && !path.Contains('\\') ? "/" : "\\");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 180;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<LogicalFile>> ReadFilesAsync(string connectionString, string backupFile)
    {
        var files = new List<LogicalFile>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"RESTORE FILELISTONLY FROM DISK = N'{backupFile}'";
        await using var reader = await command.ExecuteReaderAsync();
        var nameOrdinal = reader.GetOrdinal("LogicalName");
        var typeOrdinal = reader.GetOrdinal("Type");
        while (await reader.ReadAsync())
        {
            files.Add(new LogicalFile(reader.GetString(nameOrdinal), reader.GetString(typeOrdinal) == "L"));
        }

        return files;
    }

    private sealed record LogicalFile(string LogicalName, bool IsLog);

    private sealed record ServerState(string ServerConnectionString, string BackupFile, string DataPath, List<LogicalFile> Files);
}

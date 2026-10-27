using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Audit;

public class AuditIntegrationTests(SqlServerFixture sql) : IntegrationTestBase(sql)
{
    private readonly List<string> _logins = [];

    private enum Outcome { Approved }

    private async Task RecordAsync(string action = "item.approved", int count = 1)
    {
        using var scope = Factory.Services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        await log.RecordAsync(
            action, new AuditTarget("item", Guid.NewGuid()),
            AuditDetails.Empty.WithEnum("outcome", Outcome.Approved).WithCount("count", count));
    }

    private async Task<AuditVerificationResult> VerifyAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAsync();
    }

    private async Task<List<AuditEvent>> ReadEventsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }

    private async Task ExecuteAsync(string connectionString, string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Events_form_a_chain_starting_at_the_genesis_hash()
    {
        await RecordAsync();
        await RecordAsync("item.rejected");

        var events = await ReadEventsAsync();

        events.Should().HaveCount(2);
        events[0].PrevHash.Should().Equal(AuditHasher.GenesisHash);
        events[1].PrevHash.Should().Equal(events[0].Hash);
        events[1].Hash.Should().Equal(AuditHasher.ComputeHash(events[0].Hash, events[1]));
        events[0].DetailsJson.Should().Be("""{"count":1,"outcome":"Approved"}""");
        (await VerifyAsync()).Should().Be(new AuditVerificationResult(true, null, 2));
    }

    [Fact]
    public async Task Empty_chain_verifies()
    {
        (await VerifyAsync()).Should().Be(new AuditVerificationResult(true, null, 0));
    }

    [Fact]
    public async Task Concurrent_appends_do_not_fork_the_chain()
    {
        const int writers = 40;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(() => RecordAsync(count: i))));

        var events = await ReadEventsAsync();
        events.Should().HaveCount(writers);
        events.Select(x => x.PrevHash.ToHex()).Should().OnlyHaveUniqueItems();
        (await VerifyAsync()).Should().Be(new AuditVerificationResult(true, null, writers));
    }

    [Fact]
    public async Task Join_an_existing_transaction_and_roll_back_with_it()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await scope.ServiceProvider.GetRequiredService<IAuditLog>()
                .RecordAsync("item.approved", new AuditTarget("item"));
            await transaction.RollbackAsync();
        }

        (await ReadEventsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Interceptor_rejects_modifying_an_event()
    {
        await RecordAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var e = await db.AuditEvents.SingleAsync();
        e.Action = "item.rejected";

        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<AuditImmutableException>();
        FluentActions.Invoking(() => db.SaveChanges()).Should().Throw<AuditImmutableException>();
    }

    [Fact]
    public async Task Interceptor_rejects_deleting_an_event()
    {
        await RecordAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditEvents.Remove(await db.AuditEvents.SingleAsync());

        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<AuditImmutableException>();
        FluentActions.Invoking(() => db.SaveChanges()).Should().Throw<AuditImmutableException>();
    }

    [Fact]
    public async Task Verifier_detects_a_tampered_row()
    {
        await RecordAsync();
        await RecordAsync();
        await RecordAsync();
        var events = await ReadEventsAsync();

        // The test connection is the privileged identity, so it is allowed to tamper.
        await ExecuteAsync(ConnectionString, $"UPDATE AuditEvents SET Action = N'item.rejected' WHERE Id = {events[1].Id}");

        (await VerifyAsync()).Should().Be(new AuditVerificationResult(false, events[1].Id, 2));
    }

    [Fact]
    public async Task Verifier_detects_a_deleted_row()
    {
        await RecordAsync();
        await RecordAsync();
        await RecordAsync();
        var events = await ReadEventsAsync();

        await ExecuteAsync(ConnectionString, $"DELETE FROM AuditEvents WHERE Id = {events[1].Id}");

        (await VerifyAsync()).Should().Be(new AuditVerificationResult(false, events[2].Id, 2));
    }

    [Fact]
    public async Task Runtime_user_in_audit_writer_role_can_insert_and_select_but_not_update_or_delete()
    {
        await RecordAsync();

        var login = $"audit_test_{Guid.NewGuid():N}";
        var password = "Pw_" + Guid.NewGuid().ToString("N") + "!aA1";
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;
        _logins.Add(login);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF");
        // db_datawriter would normally allow UPDATE and DELETE; the DENY on audit_writer must win.
        await ExecuteAsync(
            ConnectionString,
            $"CREATE USER [{login}] FOR LOGIN [{login}]; " +
            $"ALTER ROLE audit_writer ADD MEMBER [{login}]; " +
            $"ALTER ROLE db_datareader ADD MEMBER [{login}]; ALTER ROLE db_datawriter ADD MEMBER [{login}];");

        var runtime = new SqlConnectionStringBuilder(ConnectionString)
        {
            UserID = login,
            Password = password,
            IntegratedSecurity = false,
            InitialCatalog = database,
            Pooling = false,
        }.ConnectionString;

        await ExecuteAsync(runtime, "SELECT COUNT(*) FROM AuditEvents");
        await ExecuteAsync(
            runtime,
            "INSERT INTO AuditEvents (OccurredAt, ActorType, Action, TargetType, CorrelationId, DetailsJson, PrevHash, Hash) " +
            "VALUES (SYSDATETIMEOFFSET(), N'System', N'raw.insert', N'item', N'c', N'{}', 0x01, 0x02)");

        var update = await FluentActions.Awaiting(() => ExecuteAsync(runtime, "UPDATE AuditEvents SET Action = N'x.y'"))
            .Should().ThrowAsync<SqlException>();
        update.Which.Number.Should().Be(229); // permission denied

        var delete = await FluentActions.Awaiting(() => ExecuteAsync(runtime, "DELETE FROM AuditEvents"))
            .Should().ThrowAsync<SqlException>();
        delete.Which.Number.Should().Be(229);
    }

    public override async Task DisposeAsync()
    {
        foreach (var login in _logins)
        {
            try
            {
                await ExecuteAsync(
                    new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString,
                    $"DROP LOGIN [{login}]");
            }
            catch (SqlException)
            {
                // Best effort; the test database is dropped by the fixture.
            }
        }

        await base.DisposeAsync();
    }
}

internal static class ByteArrayExtensions
{
    public static string ToHex(this byte[] bytes) => Convert.ToHexString(bytes);
}

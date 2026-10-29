using System.Reflection;
using System.Text;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using FluentAssertions;

namespace DocumentInvite.Api.Tests.Audit;

public class AuditUnitTests
{
    private enum Sample { Approved, Rejected }

    private static AuditEvent NewEvent() => new()
    {
        OccurredAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
        ActorType = ActorType.User,
        ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Action = "item.approved",
        TargetType = "item",
        TargetId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        IpAddress = "127.0.0.1",
        UserAgent = "tests",
        CorrelationId = "corr-1",
        DetailsJson = "{}",
    };

    [Fact]
    public void Details_serialise_ids_enums_and_counts_with_sorted_keys()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var json = new AuditDetails().WithCount("count", 3).WithEnum("status", Sample.Rejected).WithId("itemId", id).ToJson();

        json.Should().Be("""{"count":3,"itemId":"33333333-3333-3333-3333-333333333333","status":"Rejected"}""");
        AuditDetails.Empty.ToJson().Should().Be("{}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Email")]
    [InlineData("first name")]
    [InlineData("a-b")]
    public void Details_reject_malformed_keys(string key)
    {
        var act = () => new AuditDetails().WithCount(key, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Details_reject_duplicate_keys()
    {
        var act = () => new AuditDetails().WithCount("count", 1).WithCount("count", 2);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Details_api_cannot_accept_free_text()
    {
        var valueTypes = typeof(AuditDetails).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("With", StringComparison.Ordinal))
            .Select(m => m.GetParameters()[1].ParameterType)
            .ToList();

        valueTypes.Should().NotContain(typeof(string));
        valueTypes.Should().NotContain(typeof(object));
    }

    [Fact]
    public void Genesis_hash_is_fixed()
    {
        AuditHasher.GenesisHash.Should().HaveCount(32);
        AuditHasher.GenesisHash.Should().Equal(AuditHasher.GenesisHash.ToArray());
        Convert.ToHexString(AuditHasher.GenesisHash).Should().Be(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("DocumentInvite.Audit.Genesis.v1"))));
    }

    [Fact]
    public void Hash_is_deterministic()
    {
        AuditHasher.ComputeHash(AuditHasher.GenesisHash, NewEvent())
            .Should().Equal(AuditHasher.ComputeHash(AuditHasher.GenesisHash, NewEvent()));
    }

    [Fact]
    public void Hash_depends_on_previous_hash()
    {
        var other = new byte[32];

        AuditHasher.ComputeHash(other, NewEvent()).Should().NotEqual(AuditHasher.ComputeHash(AuditHasher.GenesisHash, NewEvent()));
    }

    [Fact]
    public void Hash_changes_when_any_field_changes()
    {
        var baseline = AuditHasher.ComputeHash(AuditHasher.GenesisHash, NewEvent());
        var mutations = new List<Action<AuditEvent>>
        {
            e => e.OccurredAt = e.OccurredAt.AddTicks(1),
            e => e.ActorType = ActorType.Party,
            e => e.ActorId = null,
            e => e.Action = "item.rejected",
            e => e.TargetType = "request",
            e => e.TargetId = null,
            e => e.IpAddress = null,
            e => e.UserAgent = "other",
            e => e.CorrelationId = "corr-2",
            e => e.DetailsJson = """{"count":1}""",
        };

        foreach (var mutate in mutations)
        {
            var e = NewEvent();
            mutate(e);
            AuditHasher.ComputeHash(AuditHasher.GenesisHash, e).Should().NotEqual(baseline);
        }
    }

    [Fact]
    public void Hash_ignores_the_offset_the_timestamp_is_expressed_in()
    {
        var a = NewEvent();
        var b = NewEvent();
        b.OccurredAt = a.OccurredAt.ToOffset(TimeSpan.FromHours(2));

        AuditHasher.ComputeHash(AuditHasher.GenesisHash, b).Should().Equal(AuditHasher.ComputeHash(AuditHasher.GenesisHash, a));
    }
}

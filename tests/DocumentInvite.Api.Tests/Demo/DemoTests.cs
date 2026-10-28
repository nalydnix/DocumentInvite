using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Demo;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Files;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentInvite.Api.Tests.Demo;

public abstract class DemoTestBase : StaffTestBase
{
    protected const string AdminEmail = "admin@demo.test";
    protected const string AdminPassword = "Demo-Admin-Passw0rd!";
    protected const string AuditorEmail = "auditor@demo.test";
    protected const string AuditorPassword = "Demo-Auditor-Passw0rd!";
    protected const string OfficerEmail = "officer@demo.test";
    protected const string OfficerPassword = "Demo-Officer-Passw0rd!";

    protected readonly FakeBlobStore Blobs = new();

    protected abstract bool DemoEnabled { get; }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(Blobs);
        services.Configure<DemoOptions>(o =>
        {
            o.Enabled = DemoEnabled;
            o.Accounts["Admin"] = new DemoAccountOptions { Email = AdminEmail, Password = AdminPassword };
            o.Accounts["LoanOfficer"] = new DemoAccountOptions { Email = OfficerEmail, Password = OfficerPassword };
            o.Accounts["Auditor"] = new DemoAccountOptions { Email = AuditorEmail, Password = AuditorPassword };
        });
    }

    protected async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected async Task<HttpClient> SignedInAsync(string email, string password)
    {
        var token = await SignInAsync(email, password);
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}

public class DemoSeedingTests : DemoTestBase
{
    protected override bool DemoEnabled => true;

    [Fact]
    public async Task Seeding_creates_the_three_accounts_and_the_sign_in_page_shows_all_three()
    {
        _ = Factory.Services; // starts the host, which seeds
        await SignInAsync(AdminEmail, AdminPassword);
        await SignInAsync(AuditorEmail, AuditorPassword);
        await SignInAsync(OfficerEmail, OfficerPassword);

        var config = await NewClient().GetFromJsonAsync<DemoConfigResponse>("/api/demo/config");
        config!.Enabled.Should().BeTrue();
        config.Logins.Select(l => l.Role).Should().BeEquivalentTo(["Admin", "LoanOfficer", "Auditor"]);
        config.Logins.Single(l => l.Role == "Admin").Should().Be(new DemoLogin("Admin", AdminEmail, AdminPassword));
    }

    [Fact]
    public async Task Seeding_creates_three_loans_in_the_expected_states_with_files_and_audit_events()
    {
        _ = Factory.Services;
        var loans = await LoansAsync();
        loans.Should().Equal(
            (DemoSeeder.SentReference, CollectionStatus.Sent),
            (DemoSeeder.PartlyUploadedReference, CollectionStatus.InProgress),
            (DemoSeeder.ApprovedReference, CollectionStatus.ReadyToComplete));

        await WithDbAsync(async db =>
        {
            var partly = await db.Loans.Where(l => l.Reference == DemoSeeder.PartlyUploadedReference)
                .SelectMany(l => l.Invites).SelectMany(i => i.Items).ToListAsync();
            partly.Count(i => i.Status == RequestItemStatus.Rejected).Should().Be(1);
            partly.Count(i => i.Status == RequestItemStatus.Submitted).Should().BeGreaterThan(0);
            partly.Count(i => i.Status == RequestItemStatus.Outstanding).Should().BeGreaterThan(0);

            var files = await db.StoredFiles.AsNoTracking().ToListAsync();
            files.Should().NotBeEmpty().And.OnlyContain(f => f.Status == StoredFileStatus.Ready);
            files.Should().OnlyContain(f => Blobs.Exists(f.BlobName));
            Blobs.Get(files[0].BlobName)!.Take(5).Should().Equal("%PDF-"u8.ToArray());

            (await db.Contacts.CountAsync()).Should().Be(3);
            (await db.OutboxEmails.CountAsync()).Should().Be(3);
            return 0;
        });

        var events = await AuditEventsAsync();
        events.Select(e => e.Action).Should().Contain(
            ["loan.created", "invite.sent", "portal.opened", "file.uploaded", "item.rejected", "item.approved"]);
        events.Where(e => e.Action != "user.created").Should().OnlyContain(e => e.ActorType == ActorType.System);
    }

    [Fact]
    public async Task Seeding_again_changes_nothing()
    {
        _ = Factory.Services;
        var before = await LoansAsync();
        var eventsBefore = (await AuditEventsAsync()).Count;

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
        }

        (await LoansAsync()).Should().Equal(before);
        (await AuditEventsAsync()).Should().HaveCount(eventsBefore);
    }

    [Fact]
    public async Task Resetting_twice_produces_the_same_three_loans_and_keeps_the_audit_log()
    {
        _ = Factory.Services;
        var admin = await SignedInAsync(AdminEmail, AdminPassword);
        var expected = await LoansAsync();
        var originalEvents = await AuditEventsAsync();
        var originalBlobs = await BlobNamesAsync();

        (await admin.PostAsync("/api/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoansAsync()).Should().Equal(expected);
        var blobsAfterFirst = await BlobNamesAsync();

        (await admin.PostAsync("/api/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoansAsync()).Should().Equal(expected);
        (await WithDbAsync(db => db.Contacts.CountAsync())).Should().Be(3);
        (await WithDbAsync(db => db.OutboxEmails.CountAsync())).Should().Be(3);

        // Files of earlier seeds are gone from blob storage; only the current ones remain.
        var current = await BlobNamesAsync();
        current.Should().HaveCount(originalBlobs.Count).And.OnlyContain(n => Blobs.Exists(n));
        originalBlobs.Concat(blobsAfterFirst).Should().OnlyContain(n => !Blobs.Exists(n));

        var events = await AuditEventsAsync();
        events.Take(originalEvents.Count).Select(e => Convert.ToHexString(e.Hash)).Should().Equal(originalEvents.Select(e => Convert.ToHexString(e.Hash)));
        var resets = events.Where(e => e.Action == "demo.reset").ToList();
        resets.Should().HaveCount(2);
        resets.Should().OnlyContain(e => e.ActorType == ActorType.User);

        using var scope = Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Only_an_admin_can_reset()
    {
        _ = Factory.Services;
        (await NewClient().PostAsync("/api/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var officer = await SignedInAsync(OfficerEmail, OfficerPassword);
        (await officer.PostAsync("/api/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var auditor = await SignedInAsync(AuditorEmail, AuditorPassword);
        (await auditor.PostAsync("/api/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private Task<List<(string Reference, CollectionStatus Status)>> LoansAsync() =>
        WithDbAsync(async db => (await db.Loans.AsNoTracking().OrderBy(l => l.Reference).ToListAsync())
            .Select(l => (l.Reference, l.Status)).ToList());

    private Task<List<string>> BlobNamesAsync() =>
        WithDbAsync(db => db.StoredFiles.AsNoTracking().Select(f => f.BlobName).ToListAsync());
}

public class DemoDisabledTests : DemoTestBase
{
    protected override bool DemoEnabled => false;

    [Fact]
    public async Task With_demo_mode_off_nothing_is_seeded_and_no_logins_are_shown()
    {
        var config = await NewClient().GetFromJsonAsync<DemoConfigResponse>("/api/demo/config");
        config!.Enabled.Should().BeFalse();
        config.Logins.Should().BeEmpty();

        (await PostLoginAsync(AdminEmail, AdminPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await WithDbAsync(db => db.Loans.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task With_demo_mode_off_reset_is_not_found()
    {
        var admin = await ClientForAsync("admin@example.com", ["Admin"]);
        (await admin.PostAsync("/api/demo/reset", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

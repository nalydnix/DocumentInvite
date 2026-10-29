using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Gdpr;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Files;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentInvite.Api.Tests.Gdpr;

public class RetentionTests : StaffTestBase
{
    private const string Preview = "/api/gdpr/retention/preview";
    private const string Run = "/api/gdpr/retention/run";

    private readonly FakeBlobStore _blobs = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(_blobs);
    }

    private sealed record SeededRequest(Guid RequestId, Guid ContactId, string BlobName);

    private async Task SetRetentionAsync(int days)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.RetentionDaysAfterCompletion, days));
    }

    /// <summary>Adds a contact (unless one is given) with an invite, an item and one file on a new request.</summary>
    private async Task<SeededRequest> SeedRequestAsync(string reference, DateTimeOffset? completedAt, Guid? contactId = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (contactId is null)
        {
            var contact = new Contact
            {
                FirstName = "Pat", LastName = "Doe", Email = $"{reference}@example.com", Phone = "+1 555 010 2030",
                PartyType = PartyType.Borrower, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Contacts.Add(contact);
            contactId = contact.Id;
        }

        var request = new Request
        {
            Reference = reference, Name = "Acme request", RequestType = "SBA", Amount = 5,
            CreatedAt = DateTimeOffset.UtcNow, CompletedAt = completedAt,
            Status = completedAt is null ? CollectionStatus.InProgress : CollectionStatus.Completed,
        };
        var item = new RequestItem
        {
            Name = "Statements", Kind = ItemKind.FileUpload, Instructions = "x", IsRequired = true,
            Status = RequestItemStatus.Approved,
        };
        var invite = new Invite
        {
            RequestId = request.Id, ContactId = contactId.Value, TokenHash = Guid.NewGuid().ToByteArray(),
            TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow), SentAt = DateTimeOffset.UtcNow, Items = [item],
        };
        request.Invites.Add(invite);
        db.Requests.Add(request);
        var blobName = $"invites/{invite.Id}/{Guid.NewGuid()}";
        _blobs.Put(blobName, "%PDF-1.4"u8.ToArray(), "application/pdf");
        db.StoredFiles.Add(new StoredFile
        {
            RequestItemId = item.Id, BlobName = blobName, OriginalFileName = "scan.pdf", ContentType = "application/pdf",
            SizeBytes = 8, Sequence = 1, Status = StoredFileStatus.Ready, CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Messages.Add(new Message { InviteId = invite.Id, AuthorType = MessageAuthorType.Party, Body = "hi", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return new SeededRequest(request.Id, contactId.Value, blobName);
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<AuditVerificationResult> VerifyAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAsync();
    }

    private static DateTimeOffset DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    [Fact]
    public async Task The_endpoints_are_admin_only()
    {
        var requester = await ClientForAsync("lo@example.com", [StaffRoles.Requester]);
        (await requester.GetAsync(Preview)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await requester.PostAsync(Run, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().GetAsync(Preview)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preview_lists_the_requests_and_file_counts_and_changes_nothing()
    {
        await SetRetentionAsync(30);
        var old = await SeedRequestAsync("OLD-1", DaysAgo(40));
        await SeedRequestAsync("NEW-1", DaysAgo(5));
        await SeedRequestAsync("OPEN-1", null);
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.GetAsync(Preview);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<RetentionPreviewResponse>())!;
        body.RetentionDays.Should().Be(30);
        body.RequestCount.Should().Be(1);
        body.FileCount.Should().Be(1);
        body.ContactCount.Should().Be(1);
        body.Requests.Items.Should().ContainSingle().Which.Should().Match<RetentionRequest>(
            l => l.Id == old.RequestId && l.Reference == "OLD-1" && l.FileCount == 1);

        (await DbAsync(db => db.Requests.CountAsync())).Should().Be(3);
        _blobs.Exists(old.BlobName).Should().BeTrue();
        (await DbAsync(db => db.Contacts.CountAsync(c => c.IsPseudonymised))).Should().Be(0);
    }

    [Fact]
    public async Task Preview_is_paginated()
    {
        await SetRetentionAsync(30);
        for (var i = 1; i <= 3; i++)
        {
            await SeedRequestAsync($"OLD-{i}", DaysAgo(40 + i));
        }

        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        var body = (await admin.GetFromJsonAsync<RetentionPreviewResponse>($"{Preview}?page=2&pageSize=2"))!;

        body.RequestCount.Should().Be(3);
        body.Requests.Total.Should().Be(3);
        body.Requests.Items.Should().ContainSingle();
        (await admin.GetAsync($"{Preview}?page=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Run_deletes_expired_requests_files_and_pseudonymises_contacts_without_requests()
    {
        await SetRetentionAsync(30);
        var old = await SeedRequestAsync("OLD-1", DaysAgo(40));
        var recent = await SeedRequestAsync("NEW-1", DaysAgo(5));
        var open = await SeedRequestAsync("OPEN-1", null);
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PostAsync(Run, null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<RetentionRunResponse>())!;
        (body.RequestsDeleted, body.FilesDeleted, body.ContactsPseudonymised).Should().Be((1, 1, 1));

        (await DbAsync(db => db.Requests.Select(l => l.Reference).OrderBy(r => r).ToListAsync()))
            .Should().Equal("NEW-1", "OPEN-1");
        (await DbAsync(db => db.Invites.CountAsync())).Should().Be(2);
        (await DbAsync(db => db.StoredFiles.CountAsync())).Should().Be(2);
        (await DbAsync(db => db.Messages.CountAsync())).Should().Be(2);
        _blobs.Exists(old.BlobName).Should().BeFalse();
        _blobs.Exists(recent.BlobName).Should().BeTrue();
        _blobs.Exists(open.BlobName).Should().BeTrue();

        var purged = await DbAsync(db => db.Contacts.AsNoTracking().SingleAsync(c => c.Id == old.ContactId));
        purged.IsPseudonymised.Should().BeTrue();
        purged.ErasedAt.Should().NotBeNull();
        purged.Email.Should().Be($"erased+{old.ContactId:N}@invalid");
        purged.Phone.Should().BeNull();
        (await DbAsync(db => db.Contacts.AsNoTracking().SingleAsync(c => c.Id == recent.ContactId)))
            .IsPseudonymised.Should().BeFalse();
    }

    [Fact]
    public async Task A_contact_with_another_request_that_stays_is_not_pseudonymised()
    {
        await SetRetentionAsync(30);
        var old = await SeedRequestAsync("OLD-1", DaysAgo(40));
        await SeedRequestAsync("NEW-1", DaysAgo(5), old.ContactId);
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var body = (await (await admin.PostAsync(Run, null)).Content.ReadFromJsonAsync<RetentionRunResponse>())!;

        body.RequestsDeleted.Should().Be(1);
        body.ContactsPseudonymised.Should().Be(0);
        (await DbAsync(db => db.Contacts.AsNoTracking().SingleAsync(c => c.Id == old.ContactId)))
            .IsPseudonymised.Should().BeFalse();
    }

    [Fact]
    public async Task Contacts_that_never_joined_a_request_are_left_alone()
    {
        await SetRetentionAsync(30);
        await SeedRequestAsync("OLD-1", DaysAgo(40));
        var lone = new Contact
        {
            FirstName = "Lone", LastName = "Wolf", Email = "lone@example.com", PartyType = PartyType.Broker,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await DbAsync(async db =>
        {
            db.Contacts.Add(lone);
            return await db.SaveChangesAsync();
        });
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        (await admin.PostAsync(Run, null)).EnsureSuccessStatusCode();

        (await DbAsync(db => db.Contacts.AsNoTracking().SingleAsync(c => c.Id == lone.Id))).IsPseudonymised.Should().BeFalse();
    }

    [Fact]
    public async Task Running_again_changes_nothing_further()
    {
        await SetRetentionAsync(30);
        await SeedRequestAsync("OLD-1", DaysAgo(40));
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        (await admin.PostAsync(Run, null)).EnsureSuccessStatusCode();
        var contactsBefore = await DbAsync(db => db.Contacts.AsNoTracking().ToListAsync());

        var again = (await (await admin.PostAsync(Run, null)).Content.ReadFromJsonAsync<RetentionRunResponse>())!;

        (again.RequestsDeleted, again.FilesDeleted, again.ContactsPseudonymised).Should().Be((0, 0, 0));
        var contactsAfter = await DbAsync(db => db.Contacts.AsNoTracking().ToListAsync());
        contactsAfter.Should().BeEquivalentTo(contactsBefore);
        var preview = (await admin.GetFromJsonAsync<RetentionPreviewResponse>(Preview))!;
        preview.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Previews_and_runs_are_audited_with_ids_and_counts_only_and_the_chain_still_verifies()
    {
        await SetRetentionAsync(30);
        var old = await SeedRequestAsync("OLD-1", DaysAgo(40));
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        (await admin.GetAsync(Preview)).EnsureSuccessStatusCode();
        (await admin.PostAsync(Run, null)).EnsureSuccessStatusCode();
        (await admin.PostAsync(Run, null)).EnsureSuccessStatusCode();

        var events = await AuditEventsAsync();
        var previewed = events.Single(e => e.Action == "retention.previewed");
        previewed.DetailsJson.Should().Be("{\"contacts\":1,\"files\":1,\"requests\":1}");
        events.Where(e => e.Action == "retention.run").Select(e => e.DetailsJson).Should().Equal(
            "{\"contacts\":1,\"files\":1,\"requests\":1}", "{\"contacts\":0,\"files\":0,\"requests\":0}");
        events.Single(e => e.Action == "request.purged").TargetId.Should().Be(old.RequestId);
        events.Single(e => e.Action == "contact.pseudonymised").TargetId.Should().Be(old.ContactId);
        events.Select(e => e.DetailsJson).Should().NotContainMatch("*Pat*").And.NotContainMatch("*OLD-1*");

        var result = await VerifyAsync();
        result.IsValid.Should().BeTrue();
        result.EventsChecked.Should().BeGreaterThan(4);
    }

    [Fact]
    public async Task Nothing_is_recorded_as_purged_when_a_blob_cannot_be_deleted()
    {
        await SetRetentionAsync(30);
        var old = await SeedRequestAsync("OLD-1", DaysAgo(40));
        _blobs.FailDeletes = true;
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PostAsync(Run, null);

        response.IsSuccessStatusCode.Should().BeFalse();
        (await DbAsync(db => db.Requests.CountAsync(l => l.Id == old.RequestId))).Should().Be(1);
        (await AuditEventsAsync()).Should().NotContain(e => e.Action == "request.purged");
    }
}

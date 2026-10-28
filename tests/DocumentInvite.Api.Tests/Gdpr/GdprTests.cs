using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public class GdprTests : StaffTestBase
{
    private readonly FakeBlobStore _blobs = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(_blobs);
    }

    private sealed record Seeded(Guid ContactId, Guid InviteId, Guid ItemId, string BlobName, byte[] Content);

    private async Task<Seeded> SeedAsync(string email = "pat@example.com")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contact = new Contact
        {
            FirstName = "Pat", LastName = "Doe", Email = email, Phone = "+1 555 010 2030",
            PartyType = PartyType.Borrower, CreatedAt = DateTimeOffset.UtcNow,
        };
        var loan = new Loan { Reference = "LN-7", Name = "Acme loan", LoanType = "SBA", Amount = 5, CreatedAt = DateTimeOffset.UtcNow };
        var fileItem = new RequestItem
        {
            Name = "Bank statements", Kind = ItemKind.FileUpload, Instructions = "x", IsRequired = true,
            Status = RequestItemStatus.Submitted, PartyNote = "my private note",
        };
        var fieldItem = new RequestItem
        {
            Name = "Years", Kind = ItemKind.FormField, FieldType = FieldType.Number, Instructions = "x",
            IsRequired = true, Status = RequestItemStatus.Submitted, FieldValue = "7",
        };
        var invite = new Invite
        {
            LoanId = loan.Id, ContactId = contact.Id, TokenHash = [1, 2, 3],
            TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            SentAt = DateTimeOffset.UtcNow, Items = [fileItem, fieldItem],
        };
        loan.Invites.Add(invite);
        var blobName = $"invites/{invite.Id}/{Guid.NewGuid()}";
        var content = "%PDF-1.4 hello"u8.ToArray();
        _blobs.Put(blobName, content, "application/pdf");
        db.Contacts.Add(contact);
        db.Loans.Add(loan);
        db.StoredFiles.Add(new StoredFile
        {
            RequestItemId = fileItem.Id, BlobName = blobName, OriginalFileName = "scan.pdf",
            StoredFileName = "LN-7_Doe_Bank-statements_1.pdf", ContentType = "application/pdf",
            SizeBytes = content.Length, Sequence = 1, Status = StoredFileStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow, UploadedAt = DateTimeOffset.UtcNow,
        });
        db.Messages.AddRange(
            new Message { InviteId = invite.Id, AuthorType = MessageAuthorType.Party, Body = "party says hi", CreatedAt = DateTimeOffset.UtcNow },
            new Message { InviteId = invite.Id, AuthorType = MessageAuthorType.Team, Body = "team replies", CreatedAt = DateTimeOffset.UtcNow },
            new Message { InviteId = invite.Id, AuthorType = MessageAuthorType.Team, Body = "internal secret", IsInternal = true, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return new Seeded(contact.Id, invite.Id, fileItem.Id, blobName, content);
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

    private static string Url(Guid id, string action) => $"/api/gdpr/contacts/{id}/{action}";

    // ---- export ----

    [Fact]
    public async Task Export_downloads_a_zip_with_the_json_and_the_files_and_is_audited()
    {
        var seeded = await SeedAsync();
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.GetAsync(Url(seeded.ContactId, "export"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("Doe_Pat_data-export.zip");

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("contact.json", "files/LN-7_Doe_Bank-statements_1.pdf");

        using var fileStream = zip.GetEntry("files/LN-7_Doe_Bank-statements_1.pdf")!.Open();
        using var copy = new MemoryStream();
        await fileStream.CopyToAsync(copy);
        copy.ToArray().Should().Equal(seeded.Content);

        using var json = await JsonDocument.ParseAsync(zip.GetEntry("contact.json")!.Open());
        var root = json.RootElement;
        root.GetProperty("contact").GetProperty("email").GetString().Should().Be("pat@example.com");
        var invite = root.GetProperty("invites").EnumerateArray().Single();
        invite.GetProperty("loanReference").GetString().Should().Be("LN-7");
        var items = invite.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("fieldValue").GetString()).Should().Contain("7");
        items.Select(i => i.GetProperty("partyNote").GetString()).Should().Contain("my private note");
        var bodies = invite.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()).ToList();
        bodies.Should().BeEquivalentTo("party says hi", "team replies");

        var events = await AuditEventsAsync();
        var exported = events.Single(e => e.Action == "gdpr.exported");
        exported.TargetId.Should().Be(seeded.ContactId);
        exported.DetailsJson.Should().NotContain("Pat").And.NotContain("example.com");
    }

    [Fact]
    public async Task Export_is_refused_for_an_auditor_and_missing_contacts_are_404()
    {
        var seeded = await SeedAsync();
        var auditor = await ClientForAsync("auditor@example.com", [StaffRoles.Auditor]);
        (await auditor.GetAsync(Url(seeded.ContactId, "export"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var officer = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        (await officer.GetAsync(Url(Guid.NewGuid(), "export"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NewClient().GetAsync(Url(seeded.ContactId, "export"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- erase ----

    [Fact]
    public async Task Erase_is_admin_only()
    {
        var seeded = await SeedAsync();
        var officer = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await officer.PostAsJsonAsync(Url(seeded.ContactId, "erase"), new { confirm = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DbAsync(db => db.Contacts.SingleAsync(c => c.Id == seeded.ContactId))).ErasedAt.Should().BeNull();
        _blobs.Exists(seeded.BlobName).Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"confirm\":false}")]
    public async Task Erase_needs_explicit_confirmation(string body)
    {
        var seeded = await SeedAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PostAsync(
            Url(seeded.ContactId, "erase"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.Contacts.SingleAsync(c => c.Id == seeded.ContactId))).ErasedAt.Should().BeNull();
        _blobs.Exists(seeded.BlobName).Should().BeTrue();
    }

    [Fact]
    public async Task Erase_removes_personal_data_keeps_the_structure_and_audits_ids_only()
    {
        var seeded = await SeedAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        var response = await admin.PostAsJsonAsync(Url(seeded.ContactId, "erase"), new { confirm = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _blobs.Exists(seeded.BlobName).Should().BeFalse();

        var contact = await DbAsync(db => db.Contacts.AsNoTracking().SingleAsync(c => c.Id == seeded.ContactId));
        contact.IsPseudonymised.Should().BeTrue();
        contact.ErasedAt.Should().NotBeNull();
        contact.FirstName.Should().Be("Erased");
        contact.LastName.Should().Be(seeded.ContactId.ToString("N")[..4].ToUpperInvariant());
        contact.Email.Should().Be($"erased+{seeded.ContactId:N}@invalid");
        contact.Phone.Should().BeNull();

        var invite = await DbAsync(db => db.Invites.AsNoTracking().Include(i => i.Items).SingleAsync(i => i.Id == seeded.InviteId));
        invite.RevokedAt.Should().NotBeNull();
        invite.Items.Should().OnlyContain(i => i.FieldValue == null && i.PartyNote == null);
        (await DbAsync(db => db.StoredFiles.CountAsync(f => f.RequestItemId == seeded.ItemId))).Should().Be(0);

        var messages = await DbAsync(db => db.Messages.AsNoTracking().Where(m => m.InviteId == seeded.InviteId).ToListAsync());
        messages.Single(m => m.AuthorType == MessageAuthorType.Party).Body.Should().BeEmpty();
        messages.Where(m => m.AuthorType == MessageAuthorType.Team).Select(m => m.Body)
            .Should().BeEquivalentTo("team replies", "internal secret");

        var erased = (await AuditEventsAsync()).Single(e => e.Action == "gdpr.erased");
        erased.TargetId.Should().Be(seeded.ContactId);
        erased.DetailsJson.Should().NotContainAny("Pat", "Doe", "example.com", "private");
    }

    [Fact]
    public async Task Audit_verification_still_passes_after_erasure()
    {
        var seeded = await SeedAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        (await admin.GetAsync(Url(seeded.ContactId, "export"))).EnsureSuccessStatusCode();
        (await VerifyAsync()).IsValid.Should().BeTrue();

        (await admin.PostAsJsonAsync(Url(seeded.ContactId, "erase"), new { confirm = true })).EnsureSuccessStatusCode();

        var result = await VerifyAsync();
        result.IsValid.Should().BeTrue();
        result.EventsChecked.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Erasing_twice_is_refused()
    {
        var seeded = await SeedAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        (await admin.PostAsJsonAsync(Url(seeded.ContactId, "erase"), new { confirm = true })).EnsureSuccessStatusCode();

        var again = await admin.PostAsJsonAsync(Url(seeded.ContactId, "erase"), new { confirm = true });

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AuditEventsAsync()).Count(e => e.Action == "gdpr.erased").Should().Be(1);
    }

    [Fact]
    public async Task Erasing_a_missing_contact_is_404()
    {
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        (await admin.PostAsJsonAsync(Url(Guid.NewGuid(), "erase"), new { confirm = true })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_erased_contact_is_exported_and_listed_under_the_pseudonym()
    {
        var seeded = await SeedAsync();
        var admin = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        (await admin.PostAsJsonAsync(Url(seeded.ContactId, "erase"), new { confirm = true })).EnsureSuccessStatusCode();
        var code = seeded.ContactId.ToString("N")[..4].ToUpperInvariant();

        var response = await admin.GetAsync(Url(seeded.ContactId, "export"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName.Should().Be($"Party-{code}_data-export.zip");
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().Equal("contact.json");
        using var reader = new StreamReader(zip.GetEntry("contact.json")!.Open());
        var text = await reader.ReadToEndAsync();
        text.Should().NotContain("Pat").And.NotContain("pat@example.com").And.NotContain("my private note");

        var contact = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{seeded.ContactId}");
        contact.GetProperty("lastName").GetString().Should().Be(code);
        contact.GetProperty("isPseudonymised").GetBoolean().Should().BeTrue();
    }
}

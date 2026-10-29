using System.IO.Compression;
using System.Net;
using System.Text;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Export;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Files;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentInvite.Api.Tests.Export;

public class ExportTests : StaffTestBase
{
    private readonly FakeBlobStore _blobs = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(_blobs);
    }

    private async Task<Guid> SeedRequestAsync(bool approveAnything = true)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new Request { Reference = "LN-7", Name = "Acme", RequestType = "SBA", Amount = 5, CreatedAt = DateTimeOffset.UtcNow };

        Invite NewInvite(Contact contact, params RequestItem[] items) => new()
        {
            RequestId = request.Id, ContactId = contact.Id, TokenHash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), SentAt = DateTimeOffset.UtcNow, Items = [.. items],
        };

        RequestItem FileItem(string name, RequestItemStatus status) =>
            new() { Name = name, Kind = ItemKind.FileUpload, Instructions = "x", Status = approveAnything ? status : RequestItemStatus.Submitted };

        RequestItem Field(string name, string value, RequestItemStatus status) =>
            new() { Name = name, Kind = ItemKind.FormField, FieldType = FieldType.Text, Instructions = "x", Status = approveAnything ? status : RequestItemStatus.Submitted, FieldValue = value };

        var ann = new Contact { FirstName = "Ann", LastName = "Lee", Email = "ann@example.com" };
        var bob = new Contact { FirstName = "Bob", LastName = "Adams", Email = "bob@example.com" };
        var statements = FileItem("Bank statements", RequestItemStatus.Approved);
        var taxes = FileItem("Tax return", RequestItemStatus.Rejected);
        var years = Field("Years in business", "7", RequestItemStatus.Approved);
        var notes = Field("Comments", "=HYPERLINK(\"x\"), \"quoted\"", RequestItemStatus.Approved);
        var pending = Field("Employees", "12", RequestItemStatus.Submitted);
        var lease = FileItem("Lease", RequestItemStatus.Approved);
        var annInvite = NewInvite(ann, statements, taxes, years, notes, pending);
        var bobInvite = NewInvite(bob, lease);
        request.Invites.Add(annInvite);
        request.Invites.Add(bobInvite);

        StoredFile Stored(Invite invite, RequestItem item, string name, int seq, StoredFileStatus status = StoredFileStatus.Ready)
        {
            var blob = $"invites/{invite.Id}/{Guid.NewGuid()}";
            _blobs.Put(blob, Encoding.UTF8.GetBytes("content of " + name), "application/pdf");
            return new StoredFile
            {
                RequestItemId = item.Id, BlobName = blob, OriginalFileName = "orig.pdf", StoredFileName = name,
                ContentType = "application/pdf", SizeBytes = 10, Sequence = seq, Status = status, CreatedAt = DateTimeOffset.UtcNow,
            };
        }

        db.Contacts.AddRange(ann, bob);
        db.Requests.Add(request);
        db.StoredFiles.AddRange(
            Stored(annInvite, statements, "LN-7_Lee_Bank-statements_1.pdf", 1),
            Stored(annInvite, statements, "LN-7_Lee_Bank-statements_2.pdf", 2),
            Stored(annInvite, statements, "LN-7_Lee_Bank-statements_3.pdf", 3, StoredFileStatus.Superseded),
            Stored(annInvite, taxes, "LN-7_Lee_Tax-return_1.pdf", 1),
            Stored(bobInvite, lease, "LN-7_Adams_Lease_1.pdf", 1));
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<HttpClient> ClientAsync(string role) => await ClientForAsync($"{role}@example.com", [role]);

    private static async Task<Dictionary<string, byte[]>> ReadZipAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        });
    }

    [Fact]
    public async Task Export_produces_request_party_folders_with_canonical_names_and_fields_csv()
    {
        var requestId = await SeedRequestAsync();
        var requester = await ClientAsync(StaffRoles.Requester);

        var response = await requester.GetAsync($"/api/requests/{requestId}/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("LN-7-approved.zip");
        var entries = await ReadZipAsync(response);

        entries.Keys.Should().BeEquivalentTo(
        [
            "LN-7/Ann-Lee/LN-7_Lee_Bank-statements_1.pdf",
            "LN-7/Ann-Lee/LN-7_Lee_Bank-statements_2.pdf",
            "LN-7/Ann-Lee/fields.csv",
            "LN-7/Bob-Adams/LN-7_Adams_Lease_1.pdf",
            "LN-7/Bob-Adams/fields.csv",
        ]);
        Encoding.UTF8.GetString(entries["LN-7/Ann-Lee/LN-7_Lee_Bank-statements_2.pdf"]).Should().Be("content of LN-7_Lee_Bank-statements_2.pdf");

        var csv = Encoding.UTF8.GetString(entries["LN-7/Ann-Lee/fields.csv"]).TrimStart('﻿');
        csv.Should().Be("Item,Answer\r\nComments,\"'=HYPERLINK(\"\"x\"\"), \"\"quoted\"\"\"\r\nYears in business,7\r\n");
        csv.Should().NotContain("Employees", "only approved answers are exported");
        Encoding.UTF8.GetString(entries["LN-7/Bob-Adams/fields.csv"]).TrimStart('﻿').Should().Be("Item,Answer\r\n");
    }

    [Fact]
    public async Task Export_is_allowed_for_admins_and_audited_with_counts_only()
    {
        var requestId = await SeedRequestAsync();
        var admin = await ClientAsync(StaffRoles.Admin);

        (await admin.GetAsync($"/api/requests/{requestId}/export")).StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = (await AuditEventsAsync()).Single(e => e.Action == "request.exported");
        audit.TargetType.Should().Be("request");
        audit.TargetId.Should().Be(requestId);
        audit.DetailsJson.Should().Contain("\"fileCount\":3").And.Contain("\"partyCount\":2").And.NotContain("Lee");
    }

    [Fact]
    public async Task Export_is_refused_for_auditors_and_anonymous_callers_without_auditing()
    {
        var requestId = await SeedRequestAsync();
        var auditor = await ClientAsync(StaffRoles.Auditor);

        (await auditor.GetAsync($"/api/requests/{requestId}/export")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewClient().GetAsync($"/api/requests/{requestId}/export")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuditEventsAsync()).Should().NotContain(e => e.Action == "request.exported");
    }

    [Fact]
    public async Task Export_of_a_request_with_nothing_approved_is_a_conflict_and_unknown_requests_are_404()
    {
        var requestId = await SeedRequestAsync(approveAnything: false);
        var requester = await ClientAsync(StaffRoles.Requester);

        (await requester.GetAsync($"/api/requests/{requestId}/export")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await requester.GetAsync($"/api/requests/{Guid.NewGuid()}/export")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditEventsAsync()).Should().NotContain(e => e.Action == "request.exported");
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@x", "'@x")]
    public void CsvCell_quotes_and_neutralises_formulas(string input, string expected) =>
        ExportService.CsvCell(input).Should().Be(expected);
}

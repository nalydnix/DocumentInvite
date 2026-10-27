using System.Security.Cryptography;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentInvite.Api.Tests.Files;

/// <summary>The upload slot, completion, conversion and read services, against SQL Server and an in-memory blob store.</summary>
public class FilesPipelineTests : IntegrationTestBase
{
    // A valid 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"u8.ToArray();

    private readonly FakeBlobStore _blobs = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(_blobs);
    }

    private sealed record Seeded(Guid InviteId, Guid ItemId);

    private async Task<Seeded> SeedAsync(
        string reference = "LN-2024-007", string lastName = "O'Brien", string itemName = "Bank Statements",
        ItemKind kind = ItemKind.FileUpload)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contact = new Contact { FirstName = "Pat", LastName = lastName, Email = $"{Guid.NewGuid():N}@example.com" };
        var loan = new Loan { Reference = reference, Name = "Loan", LoanType = "SBA", CreatedAt = DateTimeOffset.UtcNow };
        var item = new RequestItem { Name = itemName, Kind = kind, Instructions = "x", AllowMultiple = true };
        var invite = new Invite
        {
            LoanId = loan.Id,
            ContactId = contact.Id,
            TokenHash = RandomNumberGenerator.GetBytes(32),
            TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            SentAt = DateTimeOffset.UtcNow,
            Items = [item],
        };
        loan.Invites.Add(invite);
        db.Contacts.Add(contact);
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return new Seeded(invite.Id, item.Id);
    }

    private async Task<T> WithServiceAsync<T>(Func<UploadSlotService, FileCompletionService, FileReadService, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        return await action(
            sp.GetRequiredService<UploadSlotService>(),
            sp.GetRequiredService<FileCompletionService>(),
            sp.GetRequiredService<FileReadService>());
    }

    private Task<FileResult<UploadSlot>> SlotAsync(Guid itemId, string name, string type, long size) =>
        WithServiceAsync((slots, _, _) => slots.CreateAsync(itemId, name, type, size, default));

    private Task<FileResult<CompletedFile>> CompleteAsync(Guid fileId) =>
        WithServiceAsync((_, complete, _) => complete.CompleteAsync(fileId, default));

    private async Task<StoredFile> FileAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == id);
    }

    private async Task<Guid> UploadAsync(Seeded seeded, string name, string type, byte[] content)
    {
        var slot = await SlotAsync(seeded.ItemId, name, type, content.Length);
        slot.Succeeded.Should().BeTrue();
        _blobs.Put((await FileAsync(slot.Value!.FileId)).BlobName, content, type);
        return slot.Value!.FileId;
    }

    // ---- upload slot ----

    [Fact]
    public async Task A_slot_is_a_write_only_url_for_one_new_opaque_blob_valid_for_ten_minutes()
    {
        var seeded = await SeedAsync();

        var result = await SlotAsync(seeded.ItemId, "statement.pdf", "application/pdf", 1000);

        result.Succeeded.Should().BeTrue();
        var slot = result.Value!;
        var file = await FileAsync(slot.FileId);
        file.BlobName.Should().Be($"invites/{seeded.InviteId}/{slot.FileId}");
        file.Status.Should().Be(StoredFileStatus.PendingUpload);
        file.OriginalFileName.Should().Be("statement.pdf");
        _blobs.UploadUrls.Should().ContainSingle().Which.Should().Be((file.BlobName, TimeSpan.FromMinutes(10)));
        slot.UploadUrl.ToString().Should().Contain(file.BlobName);
        slot.RequiredHeaders.Should().Contain("x-ms-blob-type", "BlockBlob");
        slot.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("a.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("a.csv", "text/csv")]
    public async Task Every_allow_listed_type_gets_a_slot(string name, string type)
    {
        var seeded = await SeedAsync();

        (await SlotAsync(seeded.ItemId, name, type, 10)).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("a.exe", "application/x-msdownload")]
    [InlineData("a.html", "text/html")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.pdf", "text/html")]
    [InlineData("a.exe", "application/pdf")]
    [InlineData("a", "")]
    public async Task Types_outside_the_allow_list_are_refused_without_a_slot(string name, string type)
    {
        var seeded = await SeedAsync();

        var result = await SlotAsync(seeded.ItemId, name, type, 10);

        result.Error.Should().Be(FileError.UnsupportedType);
        _blobs.UploadUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task Files_over_25_MB_are_refused_but_exactly_25_MB_is_allowed()
    {
        var seeded = await SeedAsync();

        (await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", 25L * 1024 * 1024 + 1)).Error.Should().Be(FileError.TooLarge);
        (await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", 25L * 1024 * 1024)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_files_are_refused()
    {
        var seeded = await SeedAsync();

        (await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", 0)).Error.Should().Be(FileError.Empty);
    }

    [Fact]
    public async Task Slots_need_an_existing_file_upload_item()
    {
        var field = await SeedAsync(kind: ItemKind.FormField);

        (await SlotAsync(Guid.NewGuid(), "a.pdf", "application/pdf", 10)).Error.Should().Be(FileError.ItemNotFound);
        (await SlotAsync(field.ItemId, "a.pdf", "application/pdf", 10)).Error.Should().Be(FileError.NotAFileItem);
    }

    [Fact]
    public async Task The_stored_original_name_has_no_directory_part()
    {
        var seeded = await SeedAsync();

        var slot = await SlotAsync(seeded.ItemId, "..\\..\\secret\\evil.pdf", "application/pdf", 10);

        (await FileAsync(slot.Value!.FileId)).OriginalFileName.Should().Be("evil.pdf");
    }

    // ---- completion ----

    [Fact]
    public async Task Completing_a_pdf_passes_it_through_and_records_size_hash_and_canonical_name()
    {
        var seeded = await SeedAsync();
        var id = await UploadAsync(seeded, "statement.pdf", "application/pdf", Pdf);

        var result = await CompleteAsync(id);

        result.Succeeded.Should().BeTrue();
        var done = result.Value!;
        done.StoredFileName.Should().Be("LN-2024-007_OBrien_Bank-Statements_1.pdf");
        done.Sequence.Should().Be(1);
        done.ContentType.Should().Be("application/pdf");
        done.SizeBytes.Should().Be(Pdf.Length);
        done.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant());
        var file = await FileAsync(id);
        file.Status.Should().Be(StoredFileStatus.Ready);
        file.UploadedAt.Should().NotBeNull();
        file.Sha256.Should().Equal(SHA256.HashData(Pdf));
        _blobs.Get(file.BlobName).Should().Equal(Pdf);
    }

    [Fact]
    public async Task Completing_a_png_converts_it_to_a_single_page_pdf_and_replaces_the_blob()
    {
        var seeded = await SeedAsync();
        var id = await UploadAsync(seeded, "scan.png", "image/png", Png);

        var result = await CompleteAsync(id);

        result.Succeeded.Should().BeTrue();
        var file = await FileAsync(id);
        file.ContentType.Should().Be("application/pdf");
        file.StoredFileName.Should().EndWith("_1.pdf");
        var stored = _blobs.Get(file.BlobName)!;
        stored.Take(5).Should().Equal("%PDF-"u8.ToArray());
        PageCount(stored).Should().Be(1);
        _blobs.ContentTypeOf(file.BlobName).Should().Be("application/pdf");
        file.SizeBytes.Should().Be(stored.Length);
        file.Sha256.Should().Equal(SHA256.HashData(stored));
        file.OriginalFileName.Should().Be("scan.png");
    }

    [Fact]
    public async Task Other_allowed_types_are_stored_as_they_are()
    {
        var seeded = await SeedAsync();
        var csv = "a,b\n1,2\n"u8.ToArray();
        var id = await UploadAsync(seeded, "data.csv", "text/csv", csv);

        var result = await CompleteAsync(id);

        result.Value!.StoredFileName.Should().Be("LN-2024-007_OBrien_Bank-Statements_1.csv");
        _blobs.Get((await FileAsync(id)).BlobName).Should().Equal(csv);
    }

    [Fact]
    public async Task Sequence_numbers_count_up_per_item_and_restart_for_another_item()
    {
        var first = await SeedAsync();
        var second = await SeedAsync(reference: "LN-2");

        var a = await CompleteAsync(await UploadAsync(first, "a.pdf", "application/pdf", Pdf));
        var b = await CompleteAsync(await UploadAsync(first, "b.pdf", "application/pdf", Pdf));
        var c = await CompleteAsync(await UploadAsync(second, "c.pdf", "application/pdf", Pdf));

        a.Value!.Sequence.Should().Be(1);
        b.Value!.Sequence.Should().Be(2);
        b.Value.StoredFileName.Should().EndWith("_2.pdf");
        c.Value!.Sequence.Should().Be(1);
        c.Value.StoredFileName.Should().StartWith("LN-2_");
    }

    [Fact]
    public async Task Concurrent_completions_for_one_item_get_distinct_sequence_numbers()
    {
        var seeded = await SeedAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await UploadAsync(seeded, $"f{i}.pdf", "application/pdf", Pdf));
        }

        var results = await Task.WhenAll(ids.Select(CompleteAsync));

        results.Should().OnlyContain(r => r.Succeeded);
        results.Select(r => r.Value!.Sequence).Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Completing_twice_returns_the_same_result_without_a_new_sequence()
    {
        var seeded = await SeedAsync();
        var id = await UploadAsync(seeded, "a.pdf", "application/pdf", Pdf);

        var first = await CompleteAsync(id);
        var second = await CompleteAsync(id);

        second.Value.Should().Be(first.Value);
    }

    [Fact]
    public async Task Completion_fails_when_nothing_was_uploaded_and_keeps_the_slot_usable()
    {
        var seeded = await SeedAsync();
        var slot = await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", Pdf.Length);

        (await CompleteAsync(slot.Value!.FileId)).Error.Should().Be(FileError.NotUploaded);
        (await FileAsync(slot.Value.FileId)).Status.Should().Be(StoredFileStatus.PendingUpload);
        (await CompleteAsync(Guid.NewGuid())).Error.Should().Be(FileError.FileNotFound);
    }

    [Fact]
    public async Task A_blob_whose_size_differs_from_the_declared_size_is_rejected_and_deleted()
    {
        var seeded = await SeedAsync();
        var slot = await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", 10);
        var blobName = (await FileAsync(slot.Value!.FileId)).BlobName;
        _blobs.Put(blobName, Pdf, "application/pdf");

        (await CompleteAsync(slot.Value.FileId)).Error.Should().Be(FileError.SizeMismatch);

        _blobs.Exists(blobName).Should().BeFalse();
        await FluentActions.Awaiting(() => FileAsync(slot.Value.FileId)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_blob_uploaded_with_another_content_type_is_rejected_and_deleted()
    {
        var seeded = await SeedAsync();
        var slot = await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", Pdf.Length);
        var blobName = (await FileAsync(slot.Value!.FileId)).BlobName;
        _blobs.Put(blobName, Pdf, "text/html");

        (await CompleteAsync(slot.Value.FileId)).Error.Should().Be(FileError.TypeMismatch);

        _blobs.Exists(blobName).Should().BeFalse();
    }

    [Fact]
    public async Task A_blob_whose_bytes_are_not_the_declared_type_is_rejected_and_deleted()
    {
        var seeded = await SeedAsync();
        var script = "<script>alert(1)</script>"u8.ToArray();
        var slot = await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", script.Length);
        var blobName = (await FileAsync(slot.Value!.FileId)).BlobName;
        _blobs.Put(blobName, script, "application/pdf");

        (await CompleteAsync(slot.Value.FileId)).Error.Should().Be(FileError.InvalidContent);

        _blobs.Exists(blobName).Should().BeFalse();
    }

    [Fact]
    public async Task A_corrupt_image_is_rejected_and_deleted()
    {
        var seeded = await SeedAsync();
        var bytes = Png.Take(20).ToArray();
        var slot = await SlotAsync(seeded.ItemId, "a.png", "image/png", bytes.Length);
        var blobName = (await FileAsync(slot.Value!.FileId)).BlobName;
        _blobs.Put(blobName, bytes, "image/png");

        (await CompleteAsync(slot.Value.FileId)).Error.Should().Be(FileError.InvalidContent);

        _blobs.Exists(blobName).Should().BeFalse();
    }

    [Fact]
    public async Task Completion_is_audited_with_ids_only()
    {
        var seeded = await SeedAsync();
        var id = await UploadAsync(seeded, "a.pdf", "application/pdf", Pdf);
        await CompleteAsync(id);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.Action == "file.uploaded").ToListAsync();

        var audit = events.Should().ContainSingle().Subject;
        audit.TargetId.Should().Be(id);
        audit.DetailsJson.Should().NotContain("a.pdf").And.NotContain("Brien");
    }

    // ---- reads ----

    [Fact]
    public async Task A_write_to_the_upload_blob_after_completion_does_not_change_what_the_read_url_serves()
    {
        var seeded = await SeedAsync();
        var id = await UploadAsync(seeded, "a.pdf", "application/pdf", Pdf);
        var uploadBlobName = (await FileAsync(id)).BlobName;
        await CompleteAsync(id);

        _blobs.Put(uploadBlobName, "tampered"u8.ToArray(), "application/pdf"); // the still-valid upload SAS

        var file = await FileAsync(id);
        var read = await WithServiceAsync((_, _, r) => r.CreateReadUrlAsync(id, download: false, default));
        file.BlobName.Should().NotBe(uploadBlobName);
        read.Value!.Url.ToString().Should().Contain(file.BlobName).And.NotContain(uploadBlobName);
        _blobs.Get(file.BlobName).Should().Equal(Pdf);
        file.Sha256.Should().Equal(SHA256.HashData(Pdf));
    }

    [Fact]
    public async Task Preview_and_download_urls_are_valid_for_five_minutes_and_audited()
    {
        var seeded = await SeedAsync();
        var id = await UploadAsync(seeded, "a.pdf", "application/pdf", Pdf);
        await CompleteAsync(id);
        var file = await FileAsync(id);

        var preview = await WithServiceAsync((_, _, read) => read.CreateReadUrlAsync(id, download: false, default));
        var download = await WithServiceAsync((_, _, read) => read.CreateReadUrlAsync(id, download: true, default));

        preview.Succeeded.Should().BeTrue();
        download.Value!.FileName.Should().Be("LN-2024-007_OBrien_Bank-Statements_1.pdf");
        _blobs.ReadUrls.Should().Equal(
            (file.BlobName, TimeSpan.FromMinutes(5), null),
            (file.BlobName, TimeSpan.FromMinutes(5), "LN-2024-007_OBrien_Bank-Statements_1.pdf"));
        preview.Value!.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Action).ToListAsync();
        actions.Should().EndWith(["file.previewed", "file.downloaded"]);
    }

    [Fact]
    public async Task Read_urls_are_only_issued_for_completed_files()
    {
        var seeded = await SeedAsync();
        var slot = await SlotAsync(seeded.ItemId, "a.pdf", "application/pdf", 10);

        var pending = await WithServiceAsync((_, _, read) => read.CreateReadUrlAsync(slot.Value!.FileId, false, default));
        var missing = await WithServiceAsync((_, _, read) => read.CreateReadUrlAsync(Guid.NewGuid(), false, default));

        pending.Error.Should().Be(FileError.NotReady);
        missing.Error.Should().Be(FileError.FileNotFound);
        _blobs.ReadUrls.Should().BeEmpty();
    }

    // Counts "/Type /Page" objects in the generated PDF.
    private static int PageCount(byte[] pdf) =>
        System.Text.RegularExpressions.Regex.Matches(
            System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b(?!s)").Count;
}

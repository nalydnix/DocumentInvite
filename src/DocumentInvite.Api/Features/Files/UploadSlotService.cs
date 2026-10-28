using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Files;

/// <summary>Validates an intended upload and issues a write-only SAS for one new blob.</summary>
public sealed class UploadSlotService(AppDbContext db, IBlobStore blobs, TimeProvider clock)
{
    private const int MaxOriginalNameLength = 255;

    public async Task<FileResult<UploadSlot>> CreateAsync(
        Guid requestItemId, string fileName, string contentType, long sizeBytes, CancellationToken ct)
    {
        // Cheap checks first: nothing is read from the database for a file that is refused anyway.
        var type = FileTypes.Find(contentType);
        if (type is null || !FileTypes.ExtensionMatches(type, fileName))
        {
            return FileResult<UploadSlot>.Fail(FileError.UnsupportedType);
        }

        if (sizeBytes <= 0)
        {
            return FileResult<UploadSlot>.Fail(FileError.Empty);
        }

        if (sizeBytes > FileLimits.MaxBytes)
        {
            return FileResult<UploadSlot>.Fail(FileError.TooLarge);
        }

        var item = await db.RequestItems.AsNoTracking()
            .Where(i => i.Id == requestItemId)
            .Select(i => new { i.Id, i.InviteId, i.Kind })
            .SingleOrDefaultAsync(ct);
        if (item is null)
        {
            return FileResult<UploadSlot>.Fail(FileError.ItemNotFound);
        }

        if (item.Kind != ItemKind.FileUpload)
        {
            return FileResult<UploadSlot>.Fail(FileError.NotAFileItem);
        }

        var file = new StoredFile
        {
            RequestItemId = item.Id,
            OriginalFileName = SafeOriginalName(fileName),
            ContentType = type.ContentType,
            SizeBytes = sizeBytes,
            Status = StoredFileStatus.PendingUpload,
            CreatedAt = clock.GetUtcNow(),
        };
        file.BlobName = $"invites/{item.InviteId:D}/{file.Id:D}";

        // The row is saved first so every upload slot has a record that completion can find.
        db.StoredFiles.Add(file);
        await db.SaveChangesAsync(ct);

        var url = await blobs.CreateUploadUrlAsync(file.BlobName, FileLimits.UploadUrlLifetime, ct);
        return FileResult<UploadSlot>.Ok(new UploadSlot(
            file.Id,
            url.Url,
            url.ExpiresAt,
            new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = type.ContentType }));
    }

    private static string SafeOriginalName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        return name.Length > MaxOriginalNameLength ? name[^MaxOriginalNameLength..] : name;
    }
}

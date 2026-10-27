using System.Security.Cryptography;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Pdf;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Files;

/// <summary>
/// Finishes an upload on the server: re-checks the blob, hashes it, converts images to PDF and
/// assigns the canonical name and sequence number.
/// </summary>
public sealed class FileCompletionService(
    AppDbContext db,
    IBlobStore blobs,
    IImagePdfConverter converter,
    IAuditLog audit,
    TimeProvider clock)
{
    private const int SequenceAttempts = 10;

    public async Task<FileResult<CompletedFile>> CompleteAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.StoredFiles.SingleOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
        {
            return FileResult<CompletedFile>.Fail(FileError.FileNotFound);
        }

        if (file.Status == StoredFileStatus.Ready)
        {
            return FileResult<CompletedFile>.Ok(ToCompleted(file)); // completing twice is harmless
        }

        if (file.Status != StoredFileStatus.PendingUpload)
        {
            return FileResult<CompletedFile>.Fail(FileError.NotReady);
        }

        var declared = FileTypes.Find(file.ContentType)!;
        var info = await blobs.GetInfoAsync(file.BlobName, ct);
        if (info is null)
        {
            return FileResult<CompletedFile>.Fail(FileError.NotUploaded);
        }

        if (info.SizeBytes <= 0)
        {
            return await RejectAsync(file, FileError.Empty, ct);
        }

        if (info.SizeBytes > FileLimits.MaxBytes)
        {
            return await RejectAsync(file, FileError.TooLarge, ct);
        }

        if (info.SizeBytes != file.SizeBytes)
        {
            return await RejectAsync(file, FileError.SizeMismatch, ct);
        }

        if (FileTypes.Find(info.ContentType) != declared)
        {
            return await RejectAsync(file, FileError.TypeMismatch, ct);
        }

        var content = await ReadAllAsync(file.BlobName, ct);
        if (content.Length != info.SizeBytes)
        {
            return await RejectAsync(file, FileError.SizeMismatch, ct);
        }

        if (!FileTypes.SignatureMatches(declared, content))
        {
            return await RejectAsync(file, FileError.InvalidContent, ct);
        }

        var finalType = declared;
        if (declared.IsImage)
        {
            try
            {
                content = converter.Convert(content);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return await RejectAsync(file, FileError.InvalidContent, ct);
            }

            finalType = FileTypes.Find(FileTypes.Pdf)!;
            using var converted = new MemoryStream(content, writable: false);
            await blobs.WriteAsync(file.BlobName, converted, finalType.ContentType, ct);
        }

        var names = await db.RequestItems.AsNoTracking()
            .Where(i => i.Id == file.RequestItemId)
            .Join(db.Invites, i => i.InviteId, v => v.Id, (i, v) => new { ItemName = i.Name, v.LoanId, v.ContactId })
            .Join(db.Loans, x => x.LoanId, l => l.Id, (x, l) => new { x.ItemName, x.ContactId, l.Reference })
            .Join(db.Contacts, x => x.ContactId, c => c.Id, (x, c) => new { x.ItemName, x.Reference, c.LastName })
            .SingleAsync(ct);

        file.ContentType = finalType.ContentType;
        file.SizeBytes = content.Length;
        file.Sha256 = SHA256.HashData(content);
        file.Status = StoredFileStatus.Ready;
        file.UploadedAt = clock.GetUtcNow();

        for (var attempt = 1; ; attempt++)
        {
            var used = await db.StoredFiles.AsNoTracking()
                .Where(f => f.RequestItemId == file.RequestItemId && f.Sequence != null)
                .Select(f => f.Sequence!.Value)
                .ToListAsync(ct);
            file.Sequence = FileNaming.NextSequence(used);
            file.StoredFileName = FileNaming.Canonical(
                names.Reference, names.LastName, names.ItemName, file.Sequence.Value, finalType.Extension);

            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await audit.RecordAsync(
                    "file.uploaded", new AuditTarget("file", file.Id),
                    AuditDetails.Empty.WithId("requestItemId", file.RequestItemId).WithCount("sizeBytes", file.SizeBytes), ct);
                await transaction.CommitAsync(ct);
                return FileResult<CompletedFile>.Ok(ToCompleted(file));
            }
            catch (DbUpdateException) when (attempt < SequenceAttempts)
            {
                // Another upload for the same item took this sequence number first. Pick the next one.
                foreach (var added in db.ChangeTracker.Entries<AuditEvent>().Where(e => e.State == EntityState.Added).ToList())
                {
                    added.State = EntityState.Detached;
                }
            }
        }
    }

    private async Task<byte[]> ReadAllAsync(string blobName, CancellationToken ct)
    {
        await using var stream = await blobs.OpenReadAsync(blobName, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>Removes a refused upload: the blob is deleted and the file record is dropped.</summary>
    private async Task<FileResult<CompletedFile>> RejectAsync(StoredFile file, FileError error, CancellationToken ct)
    {
        await blobs.DeleteAsync(file.BlobName, ct);
        db.StoredFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        return FileResult<CompletedFile>.Fail(error);
    }

    private static CompletedFile ToCompleted(StoredFile f) =>
        new(f.Id, f.StoredFileName!, f.ContentType, f.SizeBytes, Convert.ToHexString(f.Sha256!).ToLowerInvariant(), f.Sequence!.Value);
}

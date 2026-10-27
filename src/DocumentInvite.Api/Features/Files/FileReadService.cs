using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Files;

/// <summary>Issues short-lived read SAS URLs for previewing and downloading a stored file. Every issue is audited.</summary>
public sealed class FileReadService(AppDbContext db, IBlobStore blobs, IAuditLog audit)
{
    /// <param name="download">True for an attachment named with the canonical file name; false for inline preview.</param>
    public async Task<FileResult<FileReadUrl>> CreateReadUrlAsync(Guid fileId, bool download, CancellationToken ct)
    {
        var file = await db.StoredFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
        {
            return FileResult<FileReadUrl>.Fail(FileError.FileNotFound);
        }

        if (file.Status != StoredFileStatus.Ready)
        {
            return FileResult<FileReadUrl>.Fail(FileError.NotReady);
        }

        var url = await blobs.CreateReadUrlAsync(
            file.BlobName, FileLimits.ReadUrlLifetime, download ? file.StoredFileName : null, file.ContentType, ct);

        await audit.RecordAsync(
            download ? "file.downloaded" : "file.previewed",
            new AuditTarget("file", file.Id),
            AuditDetails.Empty.WithId("requestItemId", file.RequestItemId), ct);

        return FileResult<FileReadUrl>.Ok(new FileReadUrl(url.Url, url.ExpiresAt, file.StoredFileName!, file.ContentType));
    }
}

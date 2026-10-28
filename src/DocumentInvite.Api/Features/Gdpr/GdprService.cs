using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Gdpr;

public sealed record GdprResult<T>(T? Value, bool NotFound, string? Conflict)
{
    public static GdprResult<T> Ok(T value) => new(value, false, null);
    public static GdprResult<T> Missing() => new(default, true, null);
    public static GdprResult<T> Refused(string reason) => new(default, false, reason);
}

/// <summary>A prepared export: write it to a stream when ready.</summary>
public sealed class ContactExportPackage(
    string fileName, ContactExport manifest, IReadOnlyList<(StoredFile File, string ArchivePath)> files, IBlobStore blobs)
{
    public string FileName { get; } = fileName;

    /// <summary>Writes the zip to <paramref name="output"/> as it is built, never holding it whole in memory.</summary>
    public async Task WriteToAsync(Stream output, CancellationToken ct)
    {
        await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var json = zip.CreateEntry("contact.json", CompressionLevel.Optimal);
        await using (var stream = await json.OpenAsync(ct))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, GdprService.JsonOptions, ct);
        }

        foreach (var (file, path) in files)
        {
            await using var source = await blobs.OpenReadAsync(file.BlobName, ct);
            var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
            await using var target = await entry.OpenAsync(ct);
            await source.CopyToAsync(target, ct);
        }
    }
}

/// <summary>GDPR data subject requests for one contact: export (access) and erasure.</summary>
public sealed class GdprService(AppDbContext db, IBlobStore blobs, IAuditLog audit, TimeProvider clock)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Stale = "The contact was changed by someone else. Reload it and try again.";

    /// <summary>The pseudonym code for an erased contact, for example <c>7F3A</c>. Derived from the id, so it is stable.</summary>
    public static string PseudonymCode(Guid contactId) => contactId.ToString("N")[..4].ToUpperInvariant();

    /// <summary>
    /// Replaces a contact's name, email and phone with a pseudonym and stamps <c>ErasedAt</c>. Shared by
    /// erasure and the retention purge so both leave a contact in exactly the same state.
    /// </summary>
    internal static void Pseudonymise(Contact contact, DateTimeOffset now)
    {
        contact.FirstName = "Erased";
        contact.LastName = PseudonymCode(contact.Id);
        contact.Email = $"erased+{contact.Id:N}@invalid";
        contact.Phone = null;
        contact.IsPseudonymised = true;
        contact.ErasedAt = now;
    }

    // ---- export ----

    public async Task<GdprResult<ContactExportPackage>> PrepareExportAsync(Guid contactId, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null)
        {
            return GdprResult<ContactExportPackage>.Missing();
        }

        var invites = await db.Invites.AsNoTracking()
            .Where(i => i.ContactId == contactId)
            .Include(i => i.Items)
            .OrderBy(i => i.SentAt).ThenBy(i => i.Id)
            .ToListAsync(ct);
        var loanIds = invites.Select(i => i.LoanId).Distinct().ToList();
        var loans = await db.Loans.AsNoTracking().Where(l => loanIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var inviteIds = invites.Select(i => i.Id).ToList();
        var itemIds = invites.SelectMany(i => i.Items).Select(i => i.Id).ToList();

        var files = (await db.StoredFiles.AsNoTracking()
                .Where(f => itemIds.Contains(f.RequestItemId) && f.Status == StoredFileStatus.Ready)
                .ToListAsync(ct))
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToList();

        // Internal notes are the team's own working notes, not messages exchanged with the party.
        var messages = await db.Messages.AsNoTracking()
            .Where(m => inviteIds.Contains(m.InviteId) && !m.IsInternal)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);

        var archived = new List<(StoredFile File, string ArchivePath)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contact.json" };
        var exportedFiles = new Dictionary<Guid, List<ExportedFile>>();
        foreach (var file in files)
        {
            var name = file.StoredFileName ?? FileNaming.Sanitise(file.OriginalFileName);
            var path = $"files/{name}";
            if (!used.Add(path))
            {
                path = $"files/{file.Id:N}_{name}";
                used.Add(path);
            }

            archived.Add((file, path));
            if (!exportedFiles.TryGetValue(file.RequestItemId, out var list))
            {
                exportedFiles[file.RequestItemId] = list = [];
            }

            list.Add(new ExportedFile(
                file.Id, name, file.OriginalFileName, file.ContentType, file.SizeBytes, file.UploadedAt, path));
        }

        var manifest = new ContactExport(
            clock.GetUtcNow(),
            new ExportedContact(
                contact.Id, contact.FirstName, contact.LastName, contact.Email, contact.Phone,
                contact.PartyType.ToString(), contact.IsPseudonymised, contact.ErasedAt, contact.CreatedAt),
            invites.Select(invite => new ExportedInvite(
                invite.Id,
                loans[invite.LoanId].Reference,
                loans[invite.LoanId].Name,
                invite.DueDate,
                invite.Status.ToString(),
                invite.SentAt,
                invite.RevokedAt,
                invite.PrivacyAcceptedAt,
                invite.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).Select(item => new ExportedItem(
                    item.Id, item.Name, item.Kind.ToString(), item.Status.ToString(), item.RejectionReason,
                    item.FieldValue, item.PartyNote,
                    exportedFiles.GetValueOrDefault(item.Id) ?? [])).ToList(),
                messages.Where(m => m.InviteId == invite.Id)
                    .Select(m => new ExportedMessage(m.Id, m.AuthorType.ToString(), m.Body, m.CreatedAt)).ToList()))
                .ToList());

        await audit.RecordAsync(
            "gdpr.exported", new AuditTarget("contact", contact.Id),
            AuditDetails.Empty.WithCount("invites", invites.Count).WithCount("files", archived.Count), ct);

        return GdprResult<ContactExportPackage>.Ok(
            new ContactExportPackage(ExportFileName(contact), manifest, archived, blobs));
    }

    /// <summary>An erased contact appears under the pseudonym, so the real name never reaches a file name.</summary>
    private static string ExportFileName(Contact contact) =>
        contact.IsPseudonymised
            ? $"Party-{PseudonymCode(contact.Id)}_data-export.zip"
            : $"{FileNaming.Sanitise(contact.LastName)}_{FileNaming.Sanitise(contact.FirstName)}_data-export.zip";

    // ---- erasure ----

    public async Task<GdprResult<ErasureResponse>> EraseAsync(Guid contactId, CancellationToken ct)
    {
        var contact = await db.Contacts.SingleOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null)
        {
            return GdprResult<ErasureResponse>.Missing();
        }

        if (contact.ErasedAt is not null)
        {
            return GdprResult<ErasureResponse>.Refused("This contact's data has already been erased.");
        }

        var invites = await db.Invites.Where(i => i.ContactId == contactId).Include(i => i.Items).ToListAsync(ct);
        var inviteIds = invites.Select(i => i.Id).ToList();
        var itemIds = invites.SelectMany(i => i.Items).Select(i => i.Id).ToList();
        var files = await db.StoredFiles.Where(f => itemIds.Contains(f.RequestItemId)).ToListAsync(ct);
        var partyMessages = await db.Messages
            .Where(m => inviteIds.Contains(m.InviteId) && m.AuthorType == MessageAuthorType.Party)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        var revoked = 0;
        foreach (var invite in invites)
        {
            if (invite.RevokedAt is null)
            {
                invite.RevokedAt = now;
                revoked++;
            }

            foreach (var item in invite.Items)
            {
                item.FieldValue = null;
                item.PartyNote = null;
            }
        }

        foreach (var message in partyMessages)
        {
            message.Body = string.Empty;
        }

        Pseudonymise(contact, now);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.StoredFiles.RemoveRange(files);
            await audit.RecordAsync(
                "gdpr.erased", new AuditTarget("contact", contact.Id),
                AuditDetails.Empty.WithCount("files", files.Count).WithCount("invites", revoked), ct);

            // Blobs go before the commit: if deletion fails nothing is recorded as erased and the request can be retried.
            foreach (var file in files)
            {
                await blobs.DeleteAsync(file.BlobName, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return GdprResult<ErasureResponse>.Refused(Stale);
        }

        return GdprResult<ErasureResponse>.Ok(new ErasureResponse(contact.Id, now, files.Count, revoked));
    }
}

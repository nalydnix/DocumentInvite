using System.IO.Compression;
using System.Text;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Export;

/// <summary>One approved file to place in the package.</summary>
public sealed record ExportFile(Guid FileId, string BlobName, string StoredFileName);

/// <summary>One party's approved content.</summary>
public sealed record ExportParty(string FolderName, IReadOnlyList<ExportFile> Files, IReadOnlyList<(string Item, string Value)> Fields);

/// <summary>Everything that goes into a request's package. Built from small rows only; blobs are read while streaming.</summary>
public sealed record ExportPlan(Guid RequestId, string RequestReference, IReadOnlyList<ExportParty> Parties)
{
    public int FileCount => Parties.Sum(p => p.Files.Count);
    public bool IsEmpty => Parties.Count == 0;

    /// <summary>The download name, for example <c>LN-7-approved.zip</c>.</summary>
    public string ArchiveName => $"{FileNaming.Sanitise(RequestReference)}-approved.zip";
}

/// <summary>Builds the "Download approved" package: <c>{RequestRef}/{Party}/{file}</c> plus <c>fields.csv</c> per party.</summary>
public sealed class ExportService(AppDbContext db, IBlobStore blobs, IAuditLog audit)
{
    public const string FieldsFileName = "fields.csv";

    /// <summary>Collects the approved content of a request, or null when the request does not exist.</summary>
    public async Task<ExportPlan?> PlanAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().SingleOrDefaultAsync(l => l.Id == requestId, ct);
        if (request is null)
        {
            return null;
        }

        var rows = await (
            from invite in db.Invites.AsNoTracking()
            where invite.RequestId == requestId
            join contact in db.Contacts.AsNoTracking() on invite.ContactId equals contact.Id
            orderby invite.SentAt, invite.Id
            select new { InviteId = invite.Id, contact.FirstName, contact.LastName }).ToListAsync(ct);

        var items = await (
            from item in db.RequestItems.AsNoTracking()
            where item.Status == RequestItemStatus.Approved && db.Invites.Any(i => i.Id == item.InviteId && i.RequestId == requestId)
            orderby item.SortOrder, item.Name, item.Id
            select item).ToListAsync(ct);

        var itemIds = items.Select(i => i.Id).ToList();
        var files = await db.StoredFiles.AsNoTracking()
            .Where(f => itemIds.Contains(f.RequestItemId) && f.Status == StoredFileStatus.Ready && f.StoredFileName != null)
            .OrderBy(f => f.Sequence).ThenBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .ToListAsync(ct);
        var filesByItem = files.ToLookup(f => f.RequestItemId);

        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parties = new List<ExportParty>();
        foreach (var row in rows)
        {
            var partyItems = items.Where(i => i.InviteId == row.InviteId).ToList();
            var partyFiles = new List<ExportFile>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FieldsFileName };
            foreach (var item in partyItems.Where(i => i.Kind == ItemKind.FileUpload))
            {
                foreach (var file in filesByItem[item.Id])
                {
                    partyFiles.Add(new ExportFile(file.Id, file.BlobName, UniqueName(file.StoredFileName!, usedNames)));
                }
            }

            var fields = partyItems
                .Where(i => i.Kind == ItemKind.FormField && !string.IsNullOrEmpty(i.FieldValue))
                .Select(i => (i.Name, i.FieldValue!))
                .ToList();
            if (partyFiles.Count == 0 && fields.Count == 0)
            {
                continue;
            }

            var folder = UniqueName(FileNaming.Sanitise($"{row.FirstName} {row.LastName}"), folderNames);
            parties.Add(new ExportParty(folder, partyFiles, fields));
        }

        return new ExportPlan(request.Id, request.Reference, parties);
    }

    /// <summary>Records the export in the audit trail. Done before streaming so a failure still yields a clean error response.</summary>
    public Task AuditAsync(ExportPlan plan, CancellationToken ct) =>
        audit.RecordAsync(
            "request.exported",
            new AuditTarget("request", plan.RequestId),
            AuditDetails.Empty.WithCount("fileCount", plan.FileCount).WithCount("partyCount", plan.Parties.Count),
            ct);

    /// <summary>
    /// Writes the zip to <paramref name="output"/> one entry at a time. Each blob is copied through a
    /// small buffer, so memory use does not depend on the archive size.
    /// </summary>
    public async Task WriteZipAsync(ExportPlan plan, Stream output, CancellationToken ct)
    {
        var root = FileNaming.Sanitise(plan.RequestReference);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var party in plan.Parties)
        {
            foreach (var file in party.Files)
            {
                var entry = zip.CreateEntry($"{root}/{party.FolderName}/{file.StoredFileName}", CompressionLevel.NoCompression);
                await using var entryStream = entry.Open();
                await using var blob = await blobs.OpenReadAsync(file.BlobName, ct);
                await blob.CopyToAsync(entryStream, ct);
            }

            var csv = zip.CreateEntry($"{root}/{party.FolderName}/{FieldsFileName}", CompressionLevel.Optimal);
            await using var csvStream = csv.Open();
            await csvStream.WriteAsync(BuildFieldsCsv(party.Fields), ct);
        }
    }

    private static byte[] BuildFieldsCsv(IEnumerable<(string Item, string Value)> fields)
    {
        var builder = new StringBuilder("Item,Answer\r\n");
        foreach (var (item, value) in fields)
        {
            builder.Append(CsvCell(item)).Append(',').Append(CsvCell(value)).Append("\r\n");
        }

        // A byte-order mark makes Excel read the file as UTF-8.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    /// <summary>Quotes a cell and neutralises spreadsheet formulas (a leading = + - @ or tab).</summary>
    public static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}{extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}

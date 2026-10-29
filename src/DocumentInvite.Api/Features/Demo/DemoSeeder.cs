using System.Security.Cryptography;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Demo;

/// <summary>
/// Creates the demo accounts, sample contacts and three requests (just sent, partly uploaded with one
/// rejected item, fully approved) with sample PDFs and the matching audit events. Seeding is
/// idempotent: accounts that exist are left alone and the requests are created only when missing.
/// Every seeded event is attributed to the System actor.
/// </summary>
public sealed class DemoSeeder(
    AppDbContext db,
    UserManager<AppUser> users,
    DemoOutboxEmailSender email,
    IPortalLinkBuilder links,
    IBlobStore blobs,
    IOptions<DemoOptions> options,
    TimeProvider clock,
    ILogger<DemoSeeder> logger)
{
    public const string SentReference = "DEMO-1001";
    public const string PartlyUploadedReference = "DEMO-1002";
    public const string ApprovedReference = "DEMO-1003";

    public static readonly string[] References = [SentReference, PartlyUploadedReference, ApprovedReference];

    private const int ReminderIntervalDays = 7;

    private sealed record ItemSpec(
        string Name, ItemKind Kind, RequestItemStatus Status, bool Required = true, bool Multiple = false,
        FieldType? Field = null, string? Value = null, string? Reason = null, string Instructions = "");

    private sealed record RequestSpec(
        string Reference, string Name, decimal Amount, string RequestType,
        string FirstName, string LastName, string Email, PartyType PartyType,
        int SentDaysAgo, int DueInDays, ItemSpec[] Items);

    private static readonly RequestSpec[] Requests =
    [
        new(SentReference, "Harbor Bakery acquisition", 850_000m, "SBA 7(a)",
            "Maria", "Alvarez", "maria.alvarez@example.com", PartyType.Borrower, 0, 14,
            [
                new("Business tax returns (last 3 years)", ItemKind.FileUpload, RequestItemStatus.Outstanding, Multiple: true,
                    Instructions: "Complete federal returns with all schedules."),
                new("Year-to-date profit and loss statement", ItemKind.FileUpload, RequestItemStatus.Outstanding,
                    Instructions: "Prepared within the last 90 days."),
                new("Government-issued photo ID", ItemKind.FileUpload, RequestItemStatus.Outstanding,
                    Instructions: "A clear copy of the front of the ID."),
                new("Years in business", ItemKind.FormField, RequestItemStatus.Outstanding, Field: FieldType.Number),
            ]),
        new(PartlyUploadedReference, "Riverside Plaza purchase", 2_400_000m, "Commercial real estate",
            "James", "Okafor", "james.okafor@example.com", PartyType.Borrower, 5, 9,
            [
                new("Executed purchase and sale agreement", ItemKind.FileUpload, RequestItemStatus.Submitted),
                new("Current rent roll", ItemKind.FileUpload, RequestItemStatus.Rejected,
                    Reason: "The rent roll is older than 30 days. Please upload a current one.",
                    Instructions: "Dated within the last 30 days."),
                new("Trailing 12-month operating statement", ItemKind.FileUpload, RequestItemStatus.Submitted),
                new("Appraisal", ItemKind.FileUpload, RequestItemStatus.Outstanding),
                new("Purchase price", ItemKind.FormField, RequestItemStatus.Submitted, Field: FieldType.Number, Value: "2400000"),
                new("Property manager name", ItemKind.FormField, RequestItemStatus.Outstanding, Required: false, Field: FieldType.Text),
            ]),
        new(ApprovedReference, "Summit Dental practice", 1_150_000m, "SBA 7(a)",
            "Priya", "Raman", "priya.raman@example.com", PartyType.Borrower, 20, 10,
            [
                new("Business tax returns (last 3 years)", ItemKind.FileUpload, RequestItemStatus.Approved, Multiple: true),
                new("Entity documents", ItemKind.FileUpload, RequestItemStatus.Approved),
                new("Planned closing date", ItemKind.FormField, RequestItemStatus.Approved, Field: FieldType.Date, Value: "2026-12-15"),
            ]),
    ];

    /// <summary>Creates the accounts and, when missing, the sample data. Does nothing when demo mode is off.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var audit = new AuditLog(db, new SystemRequestContext(), clock);
        await SeedUsersAsync(audit, ct);
        await SeedRequestsAsync(audit, [], ct);
    }

    /// <summary>The demo accounts. An existing account is never changed.</summary>
    public async Task SeedUsersAsync(IAuditLog audit, CancellationToken ct)
    {
        foreach (var role in StaffRoles.All)
        {
            if (!options.Value.Accounts.TryGetValue(role, out var account) || !account.IsComplete)
            {
                logger.LogWarning("Demo mode is on but no complete account is configured for the {Role} role", role);
                continue;
            }

            if (await users.FindByEmailAsync(account.Email!) is not null)
            {
                continue;
            }

            var user = new AppUser
            {
                UserName = account.Email,
                Email = account.Email,
                EmailConfirmed = true,
                DisplayName = "Demo " + role,
            };

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var created = await users.CreateAsync(user, account.Password!);
            if (!created.Succeeded)
            {
                logger.LogError(
                    "The demo {Role} account could not be created: {Errors}",
                    role, string.Join(", ", created.Errors.Select(e => e.Code)));
                continue;
            }

            await users.AddToRoleAsync(user, role);
            await audit.RecordAsync(
                "user.created", new AuditTarget("user", user.Id),
                AuditDetails.Empty.WithCount("roleCount", 1), ct);
            await transaction.CommitAsync(ct);
        }
    }

    /// <summary>
    /// Creates the three sample requests when none of them exist. Blobs are written first and their
    /// names are added to <paramref name="written"/>, so a caller that rolls back its own transaction can remove them. Joins
    /// the caller's transaction when one is open.
    /// </summary>
    public async Task SeedRequestsAsync(IAuditLog audit, List<string> written, CancellationToken ct)
    {
        if (await db.Requests.AnyAsync(l => References.Contains(l.Reference), ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        Guid? creator = null;
        if (options.Value.Accounts.TryGetValue(StaffRoles.Admin, out var admin) && admin.IsComplete)
        {
            creator = (await users.FindByEmailAsync(admin.Email!))?.Id;
        }

        var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            foreach (var spec in Requests)
            {
                await SeedRequestAsync(spec, creator, now, today, audit, written, ct);
            }

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(ct);
            }
        }
        catch
        {
            if (ownTransaction is not null)
            {
                await DeleteBlobsAsync(written);
            }

            throw;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }

    }

    public async Task DeleteBlobsAsync(IEnumerable<string> blobNames)
    {
        foreach (var name in blobNames)
        {
            try
            {
                await blobs.DeleteAsync(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A demo blob could not be deleted");
            }
        }
    }

    private async Task SeedRequestAsync(
        RequestSpec spec, Guid? creator, DateTimeOffset now, DateOnly today, IAuditLog audit, List<string> written, CancellationToken ct)
    {
        var sentAt = now.AddDays(-spec.SentDaysAgo);
        var dueDate = today.AddDays(spec.DueInDays);
        var token = PortalTokens.Generate();

        var contact = new Contact
        {
            FirstName = spec.FirstName,
            LastName = spec.LastName,
            Email = spec.Email,
            PartyType = spec.PartyType,
            CreatedAt = sentAt,
        };
        var request = new Request
        {
            Reference = spec.Reference,
            Name = spec.Name,
            Amount = spec.Amount,
            RequestType = spec.RequestType,
            CreatedBy = creator,
            CreatedAt = sentAt,
        };
        var started = spec.Items.Any(i => i.Status != RequestItemStatus.Outstanding);
        var invite = new Invite
        {
            RequestId = request.Id,
            ContactId = contact.Id,
            DueDate = dueDate,
            ReminderIntervalDays = ReminderIntervalDays,
            TokenHash = PortalTokens.Hash(token),
            TokenExpiresAt = InviteService.DefaultExpiry(dueDate),
            SentAt = sentAt,
            PortalOpenedAt = started ? sentAt.AddHours(2) : null,
            PrivacyAcceptedAt = started ? sentAt.AddHours(2) : null,
        };

        var files = new List<StoredFile>();
        var messages = new List<Message>();
        var order = 0;
        foreach (var itemSpec in spec.Items)
        {
            var reviewed = itemSpec.Status is RequestItemStatus.Approved or RequestItemStatus.Rejected;
            var item = new RequestItem
            {
                InviteId = invite.Id,
                Name = itemSpec.Name,
                Kind = itemSpec.Kind,
                Instructions = itemSpec.Instructions,
                AllowMultiple = itemSpec.Multiple,
                FieldType = itemSpec.Field,
                IsRequired = itemSpec.Required,
                SortOrder = order++,
                Status = itemSpec.Status,
                RejectionReason = itemSpec.Reason,
                FieldValue = itemSpec.Kind == ItemKind.FormField && itemSpec.Status != RequestItemStatus.Outstanding
                    ? itemSpec.Value
                    : null,
                ReviewedBy = reviewed ? creator : null,
                ReviewedAt = reviewed ? now : null,
            };
            invite.Items.Add(item);

            if (itemSpec.Kind == ItemKind.FileUpload && itemSpec.Status != RequestItemStatus.Outstanding)
            {
                files.Add(await WriteFileAsync(spec, invite, item, written, now, ct));
            }

            if (itemSpec.Status == RequestItemStatus.Rejected)
            {
                messages.Add(new Message
                {
                    InviteId = invite.Id,
                    AuthorType = MessageAuthorType.System,
                    Body = $"\"{itemSpec.Name}\" was rejected: {itemSpec.Reason}",
                    CreatedAt = now,
                });
            }
        }

        request.Invites.Add(invite);
        StatusRecalculator.Apply(request);

        // The fully approved demo request stays Ready to complete: completion is an explicit staff action.

        db.Contacts.Add(contact);
        db.Requests.Add(request);
        db.StoredFiles.AddRange(files);
        db.Messages.AddRange(messages);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("request.created", new AuditTarget("request", request.Id), null, ct);
        await audit.RecordAsync(
            "invite.sent", new AuditTarget("invite", invite.Id),
            AuditDetails.Empty.WithId("requestId", request.Id).WithId("contactId", contact.Id).WithCount("itemCount", invite.Items.Count), ct);

        var content = EmailTemplates.Invite(new InviteEmailModel(
            contact.FirstName, request.Name, links.For(token), invite.DueDate, invite.Items.Count));
        await email.SendAsync(EmailMessage.From(contact.Email, content), ct);

        if (started)
        {
            await audit.RecordAsync(
                "portal.opened", new AuditTarget("invite", invite.Id), AuditDetails.Empty.WithId("requestId", request.Id), ct);
            await audit.RecordAsync(
                "portal.privacy_accepted", new AuditTarget("invite", invite.Id), AuditDetails.Empty.WithId("requestId", request.Id), ct);
        }

        foreach (var file in files)
        {
            await audit.RecordAsync(
                "file.uploaded", new AuditTarget("file", file.Id),
                AuditDetails.Empty.WithId("requestItemId", file.RequestItemId).WithCount("sizeBytes", file.SizeBytes), ct);
        }

        foreach (var item in invite.Items.Where(i => i.Kind == ItemKind.FormField && i.FieldValue is not null))
        {
            await audit.RecordAsync(
                "portal.field_saved", new AuditTarget("request_item", item.Id), AuditDetails.Empty.WithId("inviteId", invite.Id), ct);
        }

        foreach (var item in invite.Items.Where(i => i.Status is RequestItemStatus.Approved or RequestItemStatus.Rejected))
        {
            await audit.RecordAsync(
                item.Status == RequestItemStatus.Approved ? "item.approved" : "item.rejected",
                new AuditTarget("item", item.Id),
                AuditDetails.Empty.WithId("requestId", request.Id).WithId("inviteId", invite.Id), ct);
        }
    }

    private async Task<StoredFile> WriteFileAsync(
        RequestSpec spec, Invite invite, RequestItem item, List<string> written, DateTimeOffset now, CancellationToken ct)
    {
        var fileId = Guid.NewGuid();
        var blobName = $"invites/{invite.Id}/{fileId:D}";
        var bytes = SamplePdf.Create(
            item.Name,
            $"Sample document for the DocumentInvite demo ({spec.Reference}).",
            "This file contains no real data.");

        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await blobs.WriteAsync(blobName, stream, "application/pdf", ct);
        }

        written.Add(blobName);
        return new StoredFile
        {
            Id = fileId,
            RequestItemId = item.Id,
            BlobName = blobName,
            OriginalFileName = FileNaming.Sanitise(item.Name) + ".pdf",
            StoredFileName = FileNaming.Canonical(spec.Reference, spec.LastName, item.Name, 1, "pdf"),
            ContentType = "application/pdf",
            SizeBytes = bytes.Length,
            Sha256 = SHA256.HashData(bytes),
            Sequence = 1,
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            UploadedAt = now,
        };
    }
}

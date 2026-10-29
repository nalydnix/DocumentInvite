using System.Globalization;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Portal;

public enum PortalFailure
{
    LinkNotFound,
    LinkRevoked,
    LinkExpired,
    PasswordRequired,
    InvalidPassword,
    NotFound,
    PrivacyRequired,
    ItemLocked,
    RequestLocked,
    Invalid,
    File,
}

public sealed record PortalResult<T>(T? Value, PortalFailure? Failure, string? Field, string? Message, FileError? FileError)
{
    public bool Succeeded => Failure is null;

    public static PortalResult<T> Ok(T value) => new(value, null, null, null, null);
    public static PortalResult<T> Fail(PortalFailure failure) => new(default, failure, null, null, null);
    public static PortalResult<T> Invalid(string field, string message) => new(default, PortalFailure.Invalid, field, message, null);
    public static PortalResult<T> FromFile(FileError error) => new(default, PortalFailure.File, null, null, error);
}

/// <summary>
/// The party side of an invite. Every method takes the invite id from the portal session and
/// looks items and files up through it, so one invite's data can never be reached from another.
/// </summary>
public sealed class PortalService(
    AppDbContext db,
    InviteTokenService tokens,
    PortalSessionIssuer sessions,
    IPasswordHasher<Invite> hasher,
    IAuditLog audit,
    IRequestContext context,
    UploadSlotService slots,
    FileCompletionService completion,
    TimeProvider clock)
{
    private const int MaxAttempts = 3;

    // ---- session ----

    public async Task<PortalResult<PortalSessionResponse>> ExchangeAsync(string token, string? password, CancellationToken ct)
    {
        var lookup = await tokens.ResolveAsync(token, ct);
        if (!lookup.IsValid)
        {
            return PortalResult<PortalSessionResponse>.Fail(lookup.Failure switch
            {
                TokenLookupFailure.Revoked => PortalFailure.LinkRevoked,
                TokenLookupFailure.Expired => PortalFailure.LinkExpired,
                _ => PortalFailure.LinkNotFound,
            });
        }

        var invite = lookup.Invite!;
        var rehash = false;
        if (invite.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(password))
            {
                return PortalResult<PortalSessionResponse>.Fail(PortalFailure.PasswordRequired);
            }

            var verdict = hasher.VerifyHashedPassword(invite, invite.PasswordHash, password);
            if (verdict == PasswordVerificationResult.Failed)
            {
                return PortalResult<PortalSessionResponse>.Fail(PortalFailure.InvalidPassword);
            }

            rehash = verdict == PasswordVerificationResult.SuccessRehashNeeded;
        }

        // From here on the request acts as the party, so the audit event names them.
        context.ActAsParty(invite.ContactId);
        var inviteId = invite.Id;
        db.ChangeTracker.Clear();

        await RetryAsync(async () =>
        {
            var request = await LoadRequestAsync(inviteId, ct);
            var current = request.Invites.Single(i => i.Id == inviteId);
            var now = clock.GetUtcNow();
            if (rehash)
            {
                current.PasswordHash = hasher.HashPassword(current, password!);
            }

            if (current.PortalOpenedAt is null)
            {
                current.PortalOpenedAt = now;
                StatusRecalculator.Apply(request);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(
                "portal.opened", new AuditTarget("invite", inviteId),
                AuditDetails.Empty.WithId("requestId", request.Id), ct);
            await transaction.CommitAsync(ct);
            return true;
        });

        var session = sessions.Issue(invite);
        return PortalResult<PortalSessionResponse>.Ok(new PortalSessionResponse(session.Token, session.ExpiresAt));
    }

    // ---- reading ----

    public async Task<PortalResult<PortalInviteResponse>> GetInviteAsync(Guid inviteId, CancellationToken ct)
    {
        var invite = await db.Invites.AsNoTracking().Include(i => i.Items)
            .SingleOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
        {
            return PortalResult<PortalInviteResponse>.Fail(PortalFailure.NotFound);
        }

        var requestName = await db.Requests.AsNoTracking().Where(l => l.Id == invite.RequestId).Select(l => l.Name).SingleAsync(ct);
        var firstName = await db.Contacts.AsNoTracking().Where(c => c.Id == invite.ContactId).Select(c => c.FirstName).SingleAsync(ct);

        var itemIds = invite.Items.Select(i => i.Id).ToList();
        var files = (await db.StoredFiles.AsNoTracking()
                .Where(f => itemIds.Contains(f.RequestItemId) && f.Status == StoredFileStatus.Ready)
                .OrderBy(f => f.Sequence).ThenBy(f => f.Id)
                .ToListAsync(ct))
            .ToLookup(f => f.RequestItemId);

        var items = invite.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
            .Select(i => ToItem(i, files[i.Id].Select(f =>
                new PortalFileResponse(f.Id, f.OriginalFileName, f.SizeBytes, f.UploadedAt)).ToList()))
            .ToList();

        return PortalResult<PortalInviteResponse>.Ok(new PortalInviteResponse(
            requestName, firstName, invite.DueDate, invite.Status, invite.PrivacyAcceptedAt is not null,
            items.Count, items.Count(i => i.Status is RequestItemStatus.Submitted or RequestItemStatus.Approved), items));
    }

    // ---- privacy ----

    public async Task<PortalResult<bool>> AcceptPrivacyAsync(Guid inviteId, CancellationToken ct)
    {
        var exists = await db.Invites.AsNoTracking().AnyAsync(i => i.Id == inviteId, ct);
        if (!exists)
        {
            return PortalResult<bool>.Fail(PortalFailure.NotFound);
        }

        return await RetryAsync(async () =>
        {
            var current = await db.Invites.SingleAsync(i => i.Id == inviteId, ct);
            if (current.PrivacyAcceptedAt is null)
            {
                // Accepting twice keeps the first time.
                current.PrivacyAcceptedAt = clock.GetUtcNow();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await audit.RecordAsync(
                    "portal.privacy_accepted", new AuditTarget("invite", inviteId),
                    AuditDetails.Empty.WithId("requestId", current.RequestId), ct);
                await transaction.CommitAsync(ct);
            }

            return PortalResult<bool>.Ok(true);
        });
    }

    // ---- uploads ----

    public async Task<PortalResult<UploadSlot>> CreateUploadSlotAsync(
        Guid inviteId, Guid itemId, UploadSlotRequest input, CancellationToken ct)
    {
        var guard = await GuardAsync(inviteId, itemId, ItemKind.FileUpload, ct);
        if (guard is not null)
        {
            return PortalResult<UploadSlot>.Fail(guard.Value);
        }

        var slot = await slots.CreateAsync(itemId, input.FileName!, input.ContentType!, input.SizeBytes!.Value, ct);
        return slot.Succeeded ? PortalResult<UploadSlot>.Ok(slot.Value!) : PortalResult<UploadSlot>.FromFile(slot.Error!.Value);
    }

    public async Task<PortalResult<PortalFileResponse>> CompleteUploadAsync(
        Guid inviteId, Guid itemId, Guid fileId, CancellationToken ct)
    {
        var guard = await GuardAsync(inviteId, itemId, ItemKind.FileUpload, ct);
        if (guard is not null)
        {
            return PortalResult<PortalFileResponse>.Fail(guard.Value);
        }

        var owned = await db.StoredFiles.AsNoTracking().AnyAsync(f => f.Id == fileId && f.RequestItemId == itemId, ct);
        if (!owned)
        {
            return PortalResult<PortalFileResponse>.Fail(PortalFailure.NotFound);
        }

        var completed = await completion.CompleteAsync(fileId, ct);
        if (!completed.Succeeded)
        {
            return PortalResult<PortalFileResponse>.FromFile(completed.Error!.Value);
        }

        db.ChangeTracker.Clear();
        return await RetryAsync(async () =>
        {
            var request = await LoadRequestAsync(inviteId, ct);
            var item = request.Invites.Single(i => i.Id == inviteId).Items.Single(i => i.Id == itemId);
            var file = await db.StoredFiles.SingleAsync(f => f.Id == fileId, ct);

            if (!item.AllowMultiple)
            {
                var older = await db.StoredFiles
                    .Where(f => f.RequestItemId == itemId && f.Id != fileId && f.Status == StoredFileStatus.Ready)
                    .ToListAsync(ct);
                foreach (var previous in older)
                {
                    previous.Status = StoredFileStatus.Superseded;
                }
            }

            MarkSubmitted(item);
            StatusRecalculator.Apply(request);
            await db.SaveChangesAsync(ct);
            return PortalResult<PortalFileResponse>.Ok(
                new PortalFileResponse(file.Id, file.OriginalFileName, file.SizeBytes, file.UploadedAt));
        });
    }

    // ---- form fields and notes ----

    public async Task<PortalResult<PortalItemResponse>> SaveFieldAsync(
        Guid inviteId, Guid itemId, string? rawValue, CancellationToken ct)
    {
        var guard = await GuardAsync(inviteId, itemId, ItemKind.FormField, ct);
        if (guard is not null)
        {
            return PortalResult<PortalItemResponse>.Fail(guard.Value);
        }

        var fieldType = await db.RequestItems.AsNoTracking().Where(i => i.Id == itemId)
            .Select(i => i.FieldType).SingleAsync(ct);
        var parsed = NormaliseField(fieldType, rawValue);
        if (parsed.Error is not null)
        {
            return PortalResult<PortalItemResponse>.Invalid("value", parsed.Error);
        }

        return await RetryAsync(async () =>
        {
            var request = await LoadRequestAsync(inviteId, ct);
            var item = request.Invites.Single(i => i.Id == inviteId).Items.Single(i => i.Id == itemId);
            if (item.Status == RequestItemStatus.Approved)
            {
                return PortalResult<PortalItemResponse>.Fail(PortalFailure.ItemLocked);
            }

            item.FieldValue = parsed.Value;
            if (parsed.Value is not null)
            {
                MarkSubmitted(item);
            }
            else if (item.Status == RequestItemStatus.Submitted)
            {
                item.Status = RequestItemStatus.Outstanding;
            }

            StatusRecalculator.Apply(request);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(
                "portal.field_saved", new AuditTarget("request_item", itemId),
                AuditDetails.Empty.WithId("inviteId", inviteId), ct);
            await transaction.CommitAsync(ct);
            return PortalResult<PortalItemResponse>.Ok(ToItem(item, []));
        });
    }

    public async Task<PortalResult<PortalItemResponse>> SaveNoteAsync(
        Guid inviteId, Guid itemId, string? note, CancellationToken ct)
    {
        var guard = await GuardAsync(inviteId, itemId, kind: null, ct);
        if (guard is not null)
        {
            return PortalResult<PortalItemResponse>.Fail(guard.Value);
        }

        var value = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return await RetryAsync(async () =>
        {
            var item = await db.RequestItems.SingleAsync(i => i.Id == itemId && i.InviteId == inviteId, ct);
            if (item.Status == RequestItemStatus.Approved)
            {
                return PortalResult<PortalItemResponse>.Fail(PortalFailure.ItemLocked);
            }

            item.PartyNote = value;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync(
                "portal.note_saved", new AuditTarget("request_item", itemId),
                AuditDetails.Empty.WithId("inviteId", inviteId), ct);
            await transaction.CommitAsync(ct);
            return PortalResult<PortalItemResponse>.Ok(ToItem(item, []));
        });
    }

    // ---- helpers ----

    /// <summary>The item must belong to the session invite, be the expected kind and unlocked, and privacy must be accepted.</summary>
    private async Task<PortalFailure?> GuardAsync(Guid inviteId, Guid itemId, ItemKind? kind, CancellationToken ct)
    {
        var item = await db.RequestItems.AsNoTracking()
            .Where(i => i.Id == itemId && i.InviteId == inviteId)
            .Select(i => new { i.Kind, i.Status })
            .SingleOrDefaultAsync(ct);
        if (item is null || (kind is not null && item.Kind != kind))
        {
            return PortalFailure.NotFound;
        }

        var requestCompleted = await db.Requests.AsNoTracking()
            .AnyAsync(l => l.CompletedAt != null && l.Invites.Any(i => i.Id == inviteId), ct);
        if (requestCompleted)
        {
            return PortalFailure.RequestLocked;
        }

        var privacyAccepted = await db.Invites.AsNoTracking()
            .Where(i => i.Id == inviteId).Select(i => i.PrivacyAcceptedAt != null).SingleAsync(ct);
        if (!privacyAccepted)
        {
            return PortalFailure.PrivacyRequired;
        }

        return item.Status == RequestItemStatus.Approved ? PortalFailure.ItemLocked : null;
    }

    private static void MarkSubmitted(RequestItem item)
    {
        if (item.Status is RequestItemStatus.Outstanding or RequestItemStatus.Rejected)
        {
            item.Status = RequestItemStatus.Submitted;
            item.RejectionReason = null;
        }
    }

    private Task<Request> LoadRequestAsync(Guid inviteId, CancellationToken ct) =>
        db.Requests.Where(l => l.Invites.Any(i => i.Id == inviteId))
            .Include(l => l.Invites).ThenInclude(i => i.Items)
            .AsSplitQuery()
            .SingleAsync(ct);

    /// <summary>Another request changed the same invite or request: reload and apply the change again.</summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static PortalItemResponse ToItem(RequestItem i, IReadOnlyList<PortalFileResponse> files) => new(
        i.Id, i.Name, i.Kind, i.Instructions, i.AllowMultiple, i.FieldType, i.IsRequired, i.Status,
        i.Status == RequestItemStatus.Rejected ? i.RejectionReason : null, i.FieldValue, i.PartyNote, files);

    /// <summary>Validates a form answer by type and returns the canonical text to store (null when blank).</summary>
    public static (string? Value, string? Error) NormaliseField(FieldType? type, string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        switch (type)
        {
            case FieldType.Number:
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? (number.ToString(CultureInfo.InvariantCulture), null)
                    : (null, "Enter a number.");
            case FieldType.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
                    : (null, "Enter a date as year-month-day.");
            case FieldType.YesNo:
                return text.ToLowerInvariant() switch
                {
                    "yes" or "true" => ("Yes", null),
                    "no" or "false" => ("No", null),
                    _ => (null, "Answer yes or no."),
                };
            default:
                return (text, null);
        }
    }
}

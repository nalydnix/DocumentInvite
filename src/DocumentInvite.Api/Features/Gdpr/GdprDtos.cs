using FluentValidation;

namespace DocumentInvite.Api.Features.Gdpr;

/// <summary>The explicit confirmation an erasure needs: <c>confirm</c> must be true.</summary>
public sealed record EraseContactRequest(bool? Confirm);

public sealed class EraseContactRequestValidator : AbstractValidator<EraseContactRequest>
{
    public EraseContactRequestValidator()
    {
        RuleFor(x => x.Confirm)
            .Must(confirm => confirm == true)
            .WithMessage("Erasure must be confirmed explicitly.");
    }
}

/// <summary>What an erasure removed, as counts only.</summary>
public sealed record ErasureResponse(Guid ContactId, DateTimeOffset ErasedAt, int FilesDeleted, int InvitesRevoked);

/// <summary>The contents of <c>contact.json</c> in an export.</summary>
public sealed record ContactExport(
    DateTimeOffset ExportedAt,
    ExportedContact Contact,
    IReadOnlyList<ExportedInvite> Invites);

public sealed record ExportedContact(
    Guid Id, string FirstName, string LastName, string Email, string? Phone, string PartyType,
    bool IsPseudonymised, DateTimeOffset? ErasedAt, DateTimeOffset CreatedAt);

public sealed record ExportedInvite(
    Guid Id, string RequestReference, string RequestName, DateOnly DueDate, string Status,
    DateTimeOffset SentAt, DateTimeOffset? RevokedAt, DateTimeOffset? PrivacyAcceptedAt,
    IReadOnlyList<ExportedItem> Items, IReadOnlyList<ExportedMessage> Messages);

public sealed record ExportedItem(
    Guid Id, string Name, string Kind, string Status, string? RejectionReason,
    string? FieldValue, string? PartyNote, IReadOnlyList<ExportedFile> Files);

/// <param name="ArchivePath">Where the file sits inside the zip.</param>
public sealed record ExportedFile(
    Guid Id, string FileName, string OriginalFileName, string ContentType, long SizeBytes,
    DateTimeOffset? UploadedAt, string ArchivePath);

public sealed record ExportedMessage(Guid Id, string AuthorType, string Body, DateTimeOffset CreatedAt);

/// <summary>One request the retention purge would remove.</summary>
public sealed record RetentionRequest(Guid Id, string Reference, string Name, DateTimeOffset CompletedAt, int FileCount);

/// <summary>
/// What a retention purge would remove right now. The totals cover every eligible request; <c>Requests</c> is one page of them.
/// </summary>
public sealed record RetentionPreviewResponse(
    int RetentionDays,
    DateTimeOffset Cutoff,
    int RequestCount,
    int FileCount,
    int ContactCount,
    Common.Paging.PagedResponse<RetentionRequest> Requests);

/// <summary>What a retention purge removed, as counts only.</summary>
public sealed record RetentionRunResponse(
    int RetentionDays, DateTimeOffset Cutoff, int RequestsDeleted, int FilesDeleted, int ContactsPseudonymised);

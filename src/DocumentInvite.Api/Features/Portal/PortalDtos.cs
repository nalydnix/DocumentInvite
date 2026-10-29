using DocumentInvite.Api.Domain;
using FluentValidation;

namespace DocumentInvite.Api.Features.Portal;

public sealed record PortalSessionRequest(string? Token, string? Password);

public sealed record PortalSessionResponse(string SessionToken, DateTimeOffset ExpiresAt);

public sealed record PortalFileResponse(Guid Id, string FileName, long SizeBytes, DateTimeOffset? UploadedAt);

/// <summary>One checklist item as the party sees it. Review fields other than the rejection reason are not exposed.</summary>
public sealed record PortalItemResponse(
    Guid Id,
    string Name,
    ItemKind Kind,
    string Instructions,
    bool AllowMultiple,
    FieldType? FieldType,
    bool IsRequired,
    RequestItemStatus Status,
    string? RejectionReason,
    string? FieldValue,
    string? Note,
    IReadOnlyList<PortalFileResponse> Files);

/// <summary>The party's request: only data of the session's own invite.</summary>
public sealed record PortalInviteResponse(
    string RequestName,
    string PartyFirstName,
    DateOnly DueDate,
    CollectionStatus Status,
    bool PrivacyAccepted,
    int ItemCount,
    int CompletedItemCount,
    IReadOnlyList<PortalItemResponse> Items);

public sealed record UploadSlotRequest(string? FileName, string? ContentType, long? SizeBytes);

public sealed record CompleteUploadRequest(Guid? FileId);

public sealed record SaveFieldRequest(string? Value);

public sealed record SaveNoteRequest(string? Note);

public sealed class PortalSessionRequestValidator : AbstractValidator<PortalSessionRequest>
{
    public PortalSessionRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).MaximumLength(PortalLimits.MaxPasswordLength);
    }
}

public sealed class UploadSlotRequestValidator : AbstractValidator<UploadSlotRequest>
{
    public UploadSlotRequestValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SizeBytes).NotNull();
    }
}

public sealed class CompleteUploadRequestValidator : AbstractValidator<CompleteUploadRequest>
{
    public CompleteUploadRequestValidator() => RuleFor(x => x.FileId).NotNull().NotEqual(Guid.Empty);
}

public sealed class SaveFieldRequestValidator : AbstractValidator<SaveFieldRequest>
{
    public SaveFieldRequestValidator() => RuleFor(x => x.Value).MaximumLength(PortalLimits.MaxFieldLength);
}

public sealed class SaveNoteRequestValidator : AbstractValidator<SaveNoteRequest>
{
    public SaveNoteRequestValidator() => RuleFor(x => x.Note).MaximumLength(PortalLimits.MaxNoteLength);
}

public static class PortalLimits
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxFieldLength = 2000;
    public const int MaxNoteLength = 2000;
}

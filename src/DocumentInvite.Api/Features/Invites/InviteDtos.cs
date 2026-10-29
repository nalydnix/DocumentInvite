using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Portal;
using FluentValidation;

namespace DocumentInvite.Api.Features.Invites;

public sealed record InviteItemResponse(
    Guid Id,
    string Name,
    ItemKind Kind,
    string Instructions,
    bool AllowMultiple,
    FieldType? FieldType,
    bool IsRequired,
    int SortOrder,
    RequestItemStatus Status,
    string? RejectionReason,
    string? FieldValue,
    string? PartyNote,
    DateTimeOffset? ReviewedAt,
    IReadOnlyList<ItemFileResponse> Files);

/// <summary>An uploaded file as staff see it. Names are shown to staff only; they never reach the audit trail.</summary>
public sealed record ItemFileResponse(
    Guid Id,
    string FileName,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    int Sequence,
    DateTimeOffset? UploadedAt);

/// <summary>An invite as staff see it. The portal link is never returned: it is only emailed.</summary>
public sealed record InviteResponse(
    Guid Id,
    Guid RequestId,
    Guid ContactId,
    string PartyName,
    string PartyEmail,
    PartyType PartyType,
    DateOnly DueDate,
    int ReminderIntervalDays,
    DateTimeOffset SentAt,
    DateTimeOffset TokenExpiresAt,
    DateTimeOffset? RevokedAt,
    CollectionStatus Status,
    bool HasPassword,
    IReadOnlyList<InviteItemResponse> Items);

/// <summary>One item to request. The requester may have edited the name and instructions copied from a template.</summary>
public sealed record InviteItemRequest(
    string? Name,
    ItemKind? Kind,
    string? Instructions,
    bool AllowMultiple,
    FieldType? FieldType,
    bool IsRequired);

/// <param name="ReminderIntervalDays">Omit to use the default from the settings.</param>
/// <param name="Password">Optional portal password. Stored hashed, never returned.</param>
public sealed record SendPartyRequest(
    Guid? ContactId,
    DateOnly? DueDate,
    int? ReminderIntervalDays,
    IReadOnlyList<InviteItemRequest>? Items,
    string? Password = null);

public sealed record SendInvitesRequest(IReadOnlyList<SendPartyRequest>? Parties);

public sealed class SendInvitesRequestValidator : AbstractValidator<SendInvitesRequest>
{
    public const int MaxParties = 50;

    public SendInvitesRequestValidator()
    {
        RuleFor(x => x.Parties).NotNull().Must(p => p is { Count: > 0 })
            .WithMessage("Add at least one party.");
        RuleFor(x => x.Parties!.Count).LessThanOrEqualTo(MaxParties)
            .When(x => x.Parties is not null)
            .WithName("parties").WithMessage($"At most {MaxParties} parties can be invited at once.");
        RuleForEach(x => x.Parties).SetValidator(new SendPartyRequestValidator()!);
        RuleFor(x => x.Parties)
            .Must(parties => parties!.Where(p => p.ContactId is not null).Select(p => p.ContactId).Distinct().Count()
                == parties!.Count(p => p.ContactId is not null))
            .When(x => x.Parties is not null)
            .WithMessage("A party can only be added once.");
    }
}

public sealed class SendPartyRequestValidator : AbstractValidator<SendPartyRequest>
{
    public const int MaxItems = 100;
    public const int MaxReminderIntervalDays = 365;

    public SendPartyRequestValidator()
    {
        RuleFor(x => x.ContactId).NotNull().NotEqual(Guid.Empty);
        RuleFor(x => x.DueDate).NotNull();
        RuleFor(x => x.Password).Length(PortalLimits.MinPasswordLength, PortalLimits.MaxPasswordLength)
            .When(x => !string.IsNullOrEmpty(x.Password))
            .WithMessage($"The portal password must be {PortalLimits.MinPasswordLength} to {PortalLimits.MaxPasswordLength} characters.");
        RuleFor(x => x.ReminderIntervalDays).InclusiveBetween(1, MaxReminderIntervalDays)
            .When(x => x.ReminderIntervalDays is not null);
        RuleFor(x => x.Items).NotNull().Must(i => i is { Count: > 0 })
            .WithMessage("Select at least one item for each party.");
        RuleFor(x => x.Items!.Count).LessThanOrEqualTo(MaxItems)
            .When(x => x.Items is not null)
            .WithName("items").WithMessage($"A party can have at most {MaxItems} items.");
        RuleForEach(x => x.Items).SetValidator(new InviteItemRequestValidator()!);
    }
}

public sealed class InviteItemRequestValidator : AbstractValidator<InviteItemRequest>
{
    public InviteItemRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Instructions).MaximumLength(2000);
        RuleFor(x => x.Kind).NotNull().IsInEnum();

        RuleFor(x => x.FieldType).NotNull().WithMessage("A form field requires a field type.").IsInEnum()
            .When(x => x.Kind == ItemKind.FormField);
        RuleFor(x => x.FieldType).Null()
            .When(x => x.Kind == ItemKind.FileUpload)
            .WithMessage("A file upload item cannot have a field type.");
        RuleFor(x => x.AllowMultiple).Equal(false)
            .When(x => x.Kind == ItemKind.FormField)
            .WithMessage("Only file upload items can allow multiple files.");
    }
}

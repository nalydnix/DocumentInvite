using DocumentInvite.Api.Domain;
using FluentValidation;

namespace DocumentInvite.Api.Features.Review;

public sealed record RejectItemRequest(string? Reason);

public sealed class RejectItemRequestValidator : AbstractValidator<RejectItemRequest>
{
    public const int MaxReasonLength = 1000;

    public RejectItemRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required to reject an item.")
            .MaximumLength(MaxReasonLength);
    }
}

/// <summary>The item after review, plus the recalculated invite and request status.</summary>
public sealed record ReviewResponse(
    Guid ItemId,
    RequestItemStatus ItemStatus,
    string? RejectionReason,
    DateTimeOffset ReviewedAt,
    Guid InviteId,
    CollectionStatus InviteStatus,
    Guid RequestId,
    CollectionStatus RequestStatus);

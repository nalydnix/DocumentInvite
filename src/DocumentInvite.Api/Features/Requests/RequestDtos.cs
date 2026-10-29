using System.Text.RegularExpressions;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Invites;
using FluentValidation;

namespace DocumentInvite.Api.Features.Requests;

public sealed record RequestSummaryResponse(
    Guid Id,
    string Reference,
    string Name,
    decimal Amount,
    string RequestType,
    CollectionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int PartyCount,
    int ItemsTotal,
    int ItemsApproved,
    int PercentComplete,
    DateOnly? DueDate,
    bool IsOverdue);

public sealed record RequestResponse(
    Guid Id,
    string Reference,
    string Name,
    decimal Amount,
    string RequestType,
    CollectionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<InviteResponse> Invites);

/// <summary>Query-string parameters for the request list.</summary>
public sealed record RequestListQuery(
    string? Search,
    CollectionStatus? Status,
    int Page = PagingDefaults.DefaultPage,
    int PageSize = PagingDefaults.DefaultPageSize) : IPagedQuery;

public sealed record ReopenRequestRequest(string? Reason);

public sealed record SaveRequestRequest(string? Reference, string? Name, decimal? Amount, string? RequestType);

public sealed class RequestListQueryValidator : AbstractValidator<RequestListQuery>
{
    public RequestListQueryValidator()
    {
        Include(new PagedQueryValidator<RequestListQuery>());
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
    }
}

public sealed class ReopenRequestRequestValidator : AbstractValidator<ReopenRequestRequest>
{
    public const int MaxReasonLength = 1000;

    public ReopenRequestRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required to reopen a request.")
            .MaximumLength(MaxReasonLength);
    }
}

public sealed partial class SaveRequestRequestValidator : AbstractValidator<SaveRequestRequest>
{
    public const decimal MaxAmount = 1_000_000_000_000m;

    public SaveRequestRequestValidator()
    {
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(50)
            .Matches(ReferencePattern())
            .WithMessage("'Reference' may only contain letters, digits, '.', '_' and '-', and must start with a letter or digit.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Amount).NotNull().GreaterThan(0).LessThanOrEqualTo(MaxAmount)
            .PrecisionScale(18, 2, false);
        RuleFor(x => x.RequestType).NotEmpty().MaximumLength(100);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex ReferencePattern();
}

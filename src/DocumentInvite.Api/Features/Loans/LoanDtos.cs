using System.Text.RegularExpressions;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Invites;
using FluentValidation;

namespace DocumentInvite.Api.Features.Loans;

public sealed record LoanSummaryResponse(
    Guid Id,
    string Reference,
    string Name,
    decimal Amount,
    string LoanType,
    CollectionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int PartyCount,
    int ItemsTotal,
    int ItemsApproved,
    int PercentComplete,
    DateOnly? DueDate,
    bool IsOverdue);

public sealed record LoanResponse(
    Guid Id,
    string Reference,
    string Name,
    decimal Amount,
    string LoanType,
    CollectionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<InviteResponse> Invites);

/// <summary>Query-string parameters for the loan list.</summary>
public sealed record LoanListQuery(
    string? Search,
    CollectionStatus? Status,
    int Page = PagingDefaults.DefaultPage,
    int PageSize = PagingDefaults.DefaultPageSize) : IPagedQuery;

public sealed record ReopenLoanRequest(string? Reason);

public sealed record LoanRequest(string? Reference, string? Name, decimal? Amount, string? LoanType);

public sealed class LoanListQueryValidator : AbstractValidator<LoanListQuery>
{
    public LoanListQueryValidator()
    {
        Include(new PagedQueryValidator<LoanListQuery>());
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
    }
}

public sealed class ReopenLoanRequestValidator : AbstractValidator<ReopenLoanRequest>
{
    public const int MaxReasonLength = 1000;

    public ReopenLoanRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required to reopen a loan.")
            .MaximumLength(MaxReasonLength);
    }
}

public sealed partial class LoanRequestValidator : AbstractValidator<LoanRequest>
{
    public const decimal MaxAmount = 1_000_000_000_000m;

    public LoanRequestValidator()
    {
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(50)
            .Matches(ReferencePattern())
            .WithMessage("'Reference' may only contain letters, digits, '.', '_' and '-', and must start with a letter or digit.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Amount).NotNull().GreaterThan(0).LessThanOrEqualTo(MaxAmount)
            .PrecisionScale(18, 2, false);
        RuleFor(x => x.LoanType).NotEmpty().MaximumLength(100);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex ReferencePattern();
}

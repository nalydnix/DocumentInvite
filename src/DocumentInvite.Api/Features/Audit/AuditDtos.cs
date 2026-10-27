using DocumentInvite.Api.Common;
using FluentValidation;

namespace DocumentInvite.Api.Features.Audit;

public sealed record AuditEventResponse(
    long Id,
    DateTimeOffset OccurredAt,
    ActorType ActorType,
    Guid? ActorId,
    string Action,
    string TargetType,
    Guid? TargetId,
    string? IpAddress,
    string? UserAgent,
    string CorrelationId,
    string DetailsJson);

public sealed record AuditPage(IReadOnlyList<AuditEventResponse> Items, int Total, int Page, int PageSize);

/// <summary>Query-string filter for the audit list and its CSV export.</summary>
/// <param name="From">Inclusive start of the date range.</param>
/// <param name="To">Exclusive end of the date range.</param>
/// <param name="Action">Matches actions that start with this text, for example <c>user</c> or <c>user.created</c>.</param>
public sealed record AuditQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? ActorId,
    string? Action,
    string? TargetType,
    Guid? TargetId,
    int Page = 1,
    int PageSize = 25);

public sealed record AuditVerifyResponse(bool IsValid, long? FirstBrokenEventId, long EventsChecked);

public enum VerificationOutcome
{
    Valid,
    Broken,
}

public sealed class AuditQueryValidator : AbstractValidator<AuditQuery>
{
    public const int MaxPageSize = 100;

    public AuditQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
        RuleFor(x => x.Action).MaximumLength(100);
        RuleFor(x => x.TargetType).MaximumLength(100);
        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .When(x => x.From is not null && x.To is not null)
            .WithMessage("'To' must be after 'From'.");
    }
}

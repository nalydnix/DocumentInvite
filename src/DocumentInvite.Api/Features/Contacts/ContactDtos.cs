using System.Text.RegularExpressions;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using FluentValidation;

namespace DocumentInvite.Api.Features.Contacts;

public sealed record ContactResponse(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    PartyType PartyType,
    bool IsPseudonymised);

/// <summary>Query-string parameters for the contact list.</summary>
public sealed record ContactListQuery(
    string? Search,
    int Page = PagingDefaults.DefaultPage,
    int PageSize = PagingDefaults.DefaultPageSize) : IPagedQuery;

public sealed record ContactRequest(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    PartyType? PartyType);

public sealed class ContactListQueryValidator : AbstractValidator<ContactListQuery>
{
    public ContactListQueryValidator()
    {
        Include(new PagedQueryValidator<ContactListQuery>());
        RuleFor(x => x.Search).MaximumLength(200);
    }
}

public sealed partial class ContactRequestValidator : AbstractValidator<ContactRequest>
{
    public ContactRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Phone)
            .MaximumLength(30)
            .Must(BeAPhoneNumber)
            .When(x => !string.IsNullOrWhiteSpace(x.Phone))
            .WithMessage("'Phone' must be a valid phone number, for example +1 (555) 010-2030.");
        RuleFor(x => x.PartyType).NotNull().IsInEnum();
    }

    /// <summary>An optional leading +, then digits with common separators; 7 to 15 digits in all.</summary>
    internal static bool BeAPhoneNumber(string? phone)
    {
        var value = phone!.Trim();
        if (!PhoneShape().IsMatch(value))
        {
            return false;
        }

        var digits = value.Count(char.IsAsciiDigit);
        return digits is >= 7 and <= 15;
    }

    [GeneratedRegex(@"^\+?[0-9][0-9 ().\-]*$")]
    private static partial Regex PhoneShape();
}

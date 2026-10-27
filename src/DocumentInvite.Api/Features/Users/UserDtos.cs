using DocumentInvite.Api.Features.Auth;
using FluentValidation;

namespace DocumentInvite.Api.Features.Users;

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    DateTimeOffset? AccessExpiresAt,
    bool IsLockedOut);

public sealed record CreateUserRequest(
    string? Email,
    string? DisplayName,
    string? Password,
    IReadOnlyList<string>? Roles,
    DateTimeOffset? AccessExpiresAt);

public sealed record SetRolesRequest(IReadOnlyList<string>? Roles);

public sealed record SetAccessExpiryRequest(DateTimeOffset? AccessExpiresAt);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Roles).SetValidator(new RoleListValidator()!);
        RuleFor(x => x.AccessExpiresAt)
            .Must((request, _) => request.Roles?.Contains(StaffRoles.Auditor) == true)
            .When(x => x.AccessExpiresAt is not null)
            .WithMessage("An access expiry date can only be set for an Auditor.");
    }
}

public sealed class SetRolesRequestValidator : AbstractValidator<SetRolesRequest>
{
    public SetRolesRequestValidator() => RuleFor(x => x.Roles).SetValidator(new RoleListValidator()!);
}

/// <summary>At least one role, each a known role, none repeated.</summary>
public sealed class RoleListValidator : AbstractValidator<IReadOnlyList<string>?>
{
    public RoleListValidator()
    {
        RuleFor(x => x).NotEmpty().WithMessage("At least one role is required.");
        RuleForEach(x => x).Must(role => StaffRoles.All.Contains(role))
            .WithMessage("'{PropertyValue}' is not a known role.");
        RuleFor(x => x)
            .Must(roles => roles!.Distinct().Count() == roles!.Count)
            .When(x => x is not null)
            .WithMessage("Roles must not be repeated.");
    }
}

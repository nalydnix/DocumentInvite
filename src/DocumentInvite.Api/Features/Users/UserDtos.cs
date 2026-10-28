using DocumentInvite.Api.Features.Auth;
using FluentValidation;

namespace DocumentInvite.Api.Features.Users;

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    DateTimeOffset? AccessExpiresAt,
    bool IsLockedOut);

public sealed record CreateUserRequest(
    string? Email,
    string? DisplayName,
    string? Password,
    string? Role,
    DateTimeOffset? AccessExpiresAt);

public sealed record SetRoleRequest(string? Role);

public sealed record SetAccessExpiryRequest(DateTimeOffset? AccessExpiresAt);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Role).MustBeOneKnownRole();
        RuleFor(x => x.AccessExpiresAt)
            .Must((request, _) => request.Role == StaffRoles.Auditor)
            .When(x => x.AccessExpiresAt is not null)
            .WithMessage("An access expiry date can only be set for an Auditor.");
    }
}

public sealed class SetRoleRequestValidator : AbstractValidator<SetRoleRequest>
{
    public SetRoleRequestValidator() => RuleFor(x => x.Role).MustBeOneKnownRole();
}

public static class RoleRules
{
    /// <summary>Exactly one role, and it must be a known role.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeOneKnownRole<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("A role is required.")
            .Must(role => string.IsNullOrEmpty(role) || StaffRoles.All.Contains(role))
            .WithMessage("'{PropertyValue}' is not a known role.");
}

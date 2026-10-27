using FluentValidation;

namespace DocumentInvite.Api.Features.Settings;

public sealed record SettingsResponse(int DefaultReminderIntervalDays, int RetentionDaysAfterCompletion);

public sealed record UpdateSettingsRequest(int? DefaultReminderIntervalDays, int? RetentionDaysAfterCompletion);

public sealed class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(x => x.DefaultReminderIntervalDays).NotNull().InclusiveBetween(1, 90);
        RuleFor(x => x.RetentionDaysAfterCompletion).NotNull().InclusiveBetween(30, 3650);
    }
}

using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Settings;

public sealed class SettingsService(AppDbContext db, IAuditLog audit)
{
    public async Task<SettingsResponse> GetAsync(CancellationToken ct) => ToResponse(await LoadAsync(ct));

    /// <summary>Returns null when another change was saved first.</summary>
    public async Task<SettingsResponse?> UpdateAsync(UpdateSettingsRequest request, CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        var reminder = request.DefaultReminderIntervalDays!.Value;
        var retention = request.RetentionDaysAfterCompletion!.Value;

        if (settings.DefaultReminderIntervalDays == reminder && settings.RetentionDaysAfterCompletion == retention)
        {
            return ToResponse(settings);
        }

        var details = AuditDetails.Empty
            .WithCount("defaultReminderIntervalDays", reminder)
            .WithCount("retentionDaysAfterCompletion", retention);
        settings.DefaultReminderIntervalDays = reminder;
        settings.RetentionDaysAfterCompletion = retention;

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await audit.RecordAsync("settings.updated", new AuditTarget("settings", AppSettings.SingletonId), details, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }

        return ToResponse(settings);
    }

    private Task<AppSettings> LoadAsync(CancellationToken ct) =>
        db.Settings.SingleAsync(x => x.Id == AppSettings.SingletonId, ct);

    private static SettingsResponse ToResponse(AppSettings s) =>
        new(s.DefaultReminderIntervalDays, s.RetentionDaysAfterCompletion);
}

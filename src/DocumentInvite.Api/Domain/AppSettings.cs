namespace DocumentInvite.Api.Domain;

/// <summary>Single-row application settings.</summary>
public class AppSettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = SingletonId;
    public int RetentionDaysAfterCompletion { get; set; } = 365;
    public int DefaultReminderIntervalDays { get; set; } = 7;
    public byte[] RowVersion { get; set; } = [];
}

namespace DocumentInvite.Api.Domain;

/// <summary>
/// One execution of a background job for one time window. The unique (JobName, WindowKey) pair
/// is what makes a job idempotent: a second call for the same window finds this row and does nothing.
/// </summary>
public class JobRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobName { get; set; } = string.Empty;
    public string WindowKey { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The counts the run produced, as JSON. Never holds names, addresses or other personal data.</summary>
    public string Result { get; set; } = "{}";
}

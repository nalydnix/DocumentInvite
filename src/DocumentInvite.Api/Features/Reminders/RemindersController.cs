using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Reminders;

[ApiController]
public class RemindersController(ReminderJobService service) : ControllerBase
{
    /// <summary>Called by the hourly cron. Protected by the <c>X-Api-Key</c> header.</summary>
    [HttpPost("internal/jobs/reminders")]
    [AllowAnonymous]
    [JobApiKey]
    public async Task<ReminderRunResponse> RunScheduled(CancellationToken ct) =>
        await service.RunAsync(ReminderTrigger.Scheduled, ct);

    /// <summary>The Admin "Run now" button. Uses the same service, with a manual window.</summary>
    [HttpPost("api/jobs/reminders/run")]
    [Authorize(Policy = AuthPolicies.RequireAdmin)]
    public async Task<ReminderRunResponse> RunNow(CancellationToken ct) =>
        await service.RunAsync(ReminderTrigger.Manual, ct);
}

public static class RemindersServiceCollectionExtensions
{
    public static IServiceCollection AddReminders(this IServiceCollection services)
    {
        services.AddOptions<JobsOptions>().BindConfiguration(JobsOptions.Section);
        services.AddScoped<ReminderJobService>();
        return services;
    }
}

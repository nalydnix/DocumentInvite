namespace DocumentInvite.Api.Features.Settings;

public static class SettingsServiceCollectionExtensions
{
    public static IServiceCollection AddSettings(this IServiceCollection services) =>
        services.AddScoped<SettingsService>();
}

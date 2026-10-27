using System.Net.Http.Headers;
using DocumentInvite.Api.Domain;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Email sending, selected by <c>Email:Provider</c> (<c>Demo</c> or <c>Resend</c>).</summary>
    public static IServiceCollection AddEmail(this IServiceCollection services)
    {
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section);
        services.AddOptions<ResendOptions>().BindConfiguration(ResendOptions.Section);
        services.AddOptions<PortalOptions>().BindConfiguration(PortalOptions.Section);

        services.AddSingleton<IPortalLinkBuilder, PortalLinkBuilder>();
        services.AddScoped<OutboxStore>();
        services.AddScoped<DemoOutboxEmailSender>();
        services.AddScoped<ResendEmailSender>();

        services.AddHttpClient<ResendClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<ResendOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                throw new InvalidOperationException("Resend:ApiKey must be configured when Email:Provider is Resend.");
            }

            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });

        services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value.Provider switch
        {
            EmailProvider.Demo => sp.GetRequiredService<DemoOutboxEmailSender>(),
            EmailProvider.Resend => sp.GetRequiredService<ResendEmailSender>(),
            var other => throw new InvalidOperationException($"Unknown Email:Provider '{other}'."),
        });

        return services;
    }
}

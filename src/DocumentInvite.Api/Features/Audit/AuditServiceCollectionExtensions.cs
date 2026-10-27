using DocumentInvite.Api.Infrastructure.Persistence;

namespace DocumentInvite.Api.Features.Audit;

public static class AuditServiceCollectionExtensions
{
    public static IServiceCollection AddAudit(this IServiceCollection services)
    {
        services.AddSingleton<AuditImmutabilityInterceptor>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IAuditVerifier, AuditVerifier>();
        services.AddScoped<AuditQueryService>();
        return services;
    }
}

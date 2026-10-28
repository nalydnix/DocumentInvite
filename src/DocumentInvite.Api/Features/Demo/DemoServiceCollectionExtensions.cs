namespace DocumentInvite.Api.Features.Demo;

/// <summary>Seeds the demo accounts and sample data on startup. A failure is logged, not fatal.</summary>
public sealed class DemoSeedingService(IServiceProvider services, ILogger<DemoSeedingService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Seeding the demo data failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DemoServiceCollectionExtensions
{
    public static IServiceCollection AddDemo(this IServiceCollection services)
    {
        services.AddOptions<DemoOptions>().BindConfiguration(DemoOptions.Section);
        services.AddScoped<DemoSeeder>();
        services.AddScoped<DemoService>();
        services.AddHostedService<DemoSeedingService>();
        return services;
    }
}

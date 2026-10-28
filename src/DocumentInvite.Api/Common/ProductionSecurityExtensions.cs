using Microsoft.AspNetCore.HttpOverrides;

namespace DocumentInvite.Api.Common;

/// <summary>Settings for the browser origins that may call the API (<c>Cors__Origins__0</c>, and so on).</summary>
public sealed class CorsSettings
{
    public const string Section = "Cors";

    /// <summary>Allow-listed web origins, for example <c>https://app.example.com</c>. Empty means no cross-origin access.</summary>
    public string[] Origins { get; set; } = [];
}

/// <summary>Settings for the API reference UI (<c>Scalar__Enabled</c>).</summary>
public sealed class ScalarSettings
{
    public const string Section = "Scalar";

    /// <summary>Serves the OpenAPI document and Scalar outside Development when true. Off by default.</summary>
    public bool Enabled { get; set; }
}

/// <summary>HTTPS with HSTS, a configured CORS allow-list and security headers.</summary>
public static class ProductionSecurityExtensions
{
    private const string CorsPolicy = "WebApp";

    public static IServiceCollection AddProductionSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = (configuration.GetSection(CorsSettings.Section).Get<CorsSettings>()?.Origins ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o) && o != "*")
            .Select(o => o.Trim().TrimEnd('/'))
            .ToArray();

        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()
                    .WithExposedHeaders("X-Correlation-Id");
            }
        }));

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // App Service terminates TLS in front of the app; trust its forwarded scheme and client address.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        services.AddOptions<ScalarSettings>().BindConfiguration(ScalarSettings.Section);
        return services;
    }

    /// <summary>Forwarded headers, HTTPS redirection and HSTS (outside Development), then security headers and CORS.</summary>
    public static WebApplication UseProductionSecurity(this WebApplication app)
    {
        app.UseForwardedHeaders();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                if (!context.Request.Path.StartsWithSegments("/scalar"))
                {
                    // The API serves JSON and files only; the Scalar UI needs scripts and styles, so it is exempt.
                    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                }

                return Task.CompletedTask;
            });
            await next();
        });

        app.UseCors(CorsPolicy);
        return app;
    }

    /// <summary>OpenAPI and Scalar are served in Development, or elsewhere only when <c>Scalar:Enabled</c> is true.</summary>
    public static bool ApiDocsEnabled(this WebApplication app) =>
        app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Scalar:Enabled");
}

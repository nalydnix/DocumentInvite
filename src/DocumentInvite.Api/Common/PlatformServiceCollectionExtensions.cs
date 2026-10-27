using DocumentInvite.Api.Common.Errors;
using DocumentInvite.Api.Common.Logging;
using DocumentInvite.Api.Common.Validation;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;

namespace DocumentInvite.Api.Common;

public static class PlatformServiceCollectionExtensions
{
    public static WebApplicationBuilder AddPlatform(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, logger) =>
        {
            logger
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.With<PiiMaskingEnricher>();

            // Sinks are wrapped so that exception text is masked before it is written.
            var console = new LoggerConfiguration();
            if (context.HostingEnvironment.IsDevelopment())
            {
                console.WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}");
            }
            else
            {
                console.WriteTo.Console(new RenderedCompactJsonFormatter());
            }

            logger.WriteTo.Sink(new ExceptionMaskingSink(console.CreateLogger()));
        });

        var services = builder.Services;

        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddAudit();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
                ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
            options.UseSqlServer(connectionString);
            options.AddInterceptors(sp.GetRequiredService<AuditImmutabilityInterceptor>());
        });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["correlationId"] =
                context.HttpContext.Items[CorrelationIdMiddleware.ItemKey];
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddValidatorsFromAssemblyContaining<Program>();
        services.AddControllers(options => options.Filters.Add<ValidationFilter>())
            .ConfigureApiBehaviorOptions(options =>
                options.InvalidModelStateResponseFactory = context =>
                    ValidationProblems.Create(context.HttpContext, context.ModelState));

        return builder;
    }

    public static WebApplication UsePlatform(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging(options =>
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms");
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}

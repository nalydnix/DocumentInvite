using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Contacts;
using DocumentInvite.Api.Features.Settings;
using DocumentInvite.Api.Features.Users;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace DocumentInvite.Api.Features.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>Identity with bearer tokens, the staff roles and the authorisation policies.</summary>
    public static IServiceCollection AddStaffAuth(this IServiceCollection services)
    {
        services.AddIdentityApiEndpoints<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 12;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<AppSignInManager>();

        // Roles travel inside the access token, so keep it short-lived: a role change applies
        // after at most this long (or immediately, because it also invalidates refresh tokens).
        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(15);
            options.RefreshTokenExpiration = TimeSpan.FromHours(8);
        });

        // Bearer tokens only: no cookie scheme, so unauthenticated calls get 401, never a redirect.
        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = IdentityConstants.BearerScheme;
            options.DefaultAuthenticateScheme = IdentityConstants.BearerScheme;
            options.DefaultChallengeScheme = IdentityConstants.BearerScheme;
            options.DefaultForbidScheme = IdentityConstants.BearerScheme;
        });

        services.AddScoped<IAuthorizationHandler, AccessNotExpiredHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AccessExpiredResultHandler>();
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = Policy(null);
            options.AddPolicy(AuthPolicies.RequireAdmin, Policy([StaffRoles.Admin]));
            options.AddPolicy(AuthPolicies.RequireStaff, Policy([StaffRoles.Admin, StaffRoles.Requester]));
            options.AddPolicy(AuthPolicies.CanRead, Policy([StaffRoles.Admin, StaffRoles.Requester]));
            options.AddPolicy(AuthPolicies.CanAudit, Policy([StaffRoles.Admin, StaffRoles.Auditor]));
        });

        services.AddHostedService<AdminBootstrapper>();
        services.AddUserAdmin();
        services.AddContacts();
        services.AddSettings();
        return services;
    }

    private static AuthorizationPolicy Policy(string[]? roles)
    {
        var builder = new AuthorizationPolicyBuilder(IdentityConstants.BearerScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new AccessNotExpiredRequirement());
        if (roles is not null)
        {
            builder.RequireRole(roles);
        }

        return builder.Build();
    }

    public static WebApplication UseStaffAuth(this WebApplication app)
    {
        // Only sign-in and token refresh are exposed under /api/auth: public registration, password
        // reset, e-mail confirmation and self-service account management are disabled (Admins create users).
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/auth")
                && !path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>Maps the Identity endpoints under <c>/api/auth</c> (see <see cref="UseStaffAuth"/> for what is blocked).</summary>
    public static IEndpointRouteBuilder MapStaffAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/auth").MapIdentityApi<AppUser>();
        return endpoints;
    }
}

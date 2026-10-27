using System.Security.Claims;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Auth;

/// <summary>The signed-in account must still exist and its <c>AccessExpiresAt</c> must not have passed.</summary>
public sealed class AccessNotExpiredRequirement : IAuthorizationRequirement;

/// <summary>Checks the requirement against the database on every request, so a token cannot outlive the expiry.</summary>
public sealed class AccessNotExpiredHandler(AppDbContext db, TimeProvider clock)
    : AuthorizationHandler<AccessNotExpiredRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AccessNotExpiredRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            context.Fail();
            return;
        }

        var user = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.AccessExpiresAt })
            .FirstOrDefaultAsync();

        if (user is null || (user.AccessExpiresAt is { } expiry && expiry <= clock.GetUtcNow()))
        {
            context.Fail(new AuthorizationFailureReason(this, "Access has expired."));
            return;
        }

        context.Succeed(requirement);
    }
}

/// <summary>
/// Answers 401 (rather than 403) when an account's access has expired, so clients sign the user out.
/// Every other outcome uses the default behaviour.
/// </summary>
public sealed class AccessExpiredResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure is { } failure
            && (failure.FailedRequirements.OfType<AccessNotExpiredRequirement>().Any()
                || failure.FailureReasons.Any(r => r.Handler is AccessNotExpiredHandler)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Access expired",
                    Detail = "This account's access has expired.",
                },
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

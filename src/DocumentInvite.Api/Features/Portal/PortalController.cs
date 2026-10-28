using System.Security.Claims;
using System.Threading.RateLimiting;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Messaging;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Portal;

/// <summary>
/// The borrower portal API. Only the portal session scheme is accepted: a staff token is refused, and a
/// portal session is refused by every staff endpoint.
/// </summary>
[ApiController]
[Route("api/portal")]
[Authorize(AuthenticationSchemes = PortalAuth.Scheme, Policy = PortalAuth.Policy)]
public class PortalController(PortalService service, MessagingService messaging) : ControllerBase
{
    private Guid InviteId => Guid.Parse(User.FindFirstValue(PortalAuth.InviteClaim)!);

    /// <summary>Exchanges the link token (and the password, when one is set) for a short-lived portal session.</summary>
    [HttpPost("session")]
    [AllowAnonymous]
    [EnableRateLimiting(PortalAuth.RateLimitPolicy)]
    public async Task<IActionResult> CreateSession(PortalSessionRequest request, CancellationToken ct) =>
        ToActionResult(await service.ExchangeAsync(request.Token!, request.Password, ct));

    [HttpGet("invite")]
    public async Task<IActionResult> GetInvite(CancellationToken ct) =>
        ToActionResult(await service.GetInviteAsync(InviteId, ct));

    [HttpPost("privacy")]
    public async Task<IActionResult> AcceptPrivacy(CancellationToken ct)
    {
        var result = await service.AcceptPrivacyAsync(InviteId, ct);
        return result.Succeeded ? NoContent() : ToActionResult(result);
    }

    [HttpPost("items/{itemId:guid}/upload-slot")]
    public async Task<IActionResult> UploadSlot(Guid itemId, UploadSlotRequest request, CancellationToken ct) =>
        ToActionResult(await service.CreateUploadSlotAsync(InviteId, itemId, request, ct));

    [HttpPost("items/{itemId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid itemId, CompleteUploadRequest request, CancellationToken ct) =>
        ToActionResult(await service.CompleteUploadAsync(InviteId, itemId, request.FileId!.Value, ct));

    [HttpPut("items/{itemId:guid}/field")]
    public async Task<IActionResult> SaveField(Guid itemId, SaveFieldRequest request, CancellationToken ct) =>
        ToActionResult(await service.SaveFieldAsync(InviteId, itemId, request.Value, ct));

    [HttpPut("items/{itemId:guid}/note")]
    public async Task<IActionResult> SaveNote(Guid itemId, SaveNoteRequest request, CancellationToken ct) =>
        ToActionResult(await service.SaveNoteAsync(InviteId, itemId, request.Note, ct));

    /// <summary>The thread, newest first. Internal notes are never returned here.</summary>
    [HttpGet("messages")]
    public async Task<IActionResult> ListMessages([FromQuery] PageQuery query, CancellationToken ct) =>
        ToActionResult(await messaging.ListForPartyAsync(InviteId, query, ct));

    [HttpPost("messages")]
    public async Task<IActionResult> PostMessage(PostMessageRequest request, CancellationToken ct)
    {
        var result = await messaging.PostPartyMessageAsync(InviteId, request.Body!, ct);
        return result.Succeeded ? StatusCode(StatusCodes.Status201Created, result.Value) : ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(PortalResult<T> result)
    {
        if (result.Succeeded)
        {
            return Ok(result.Value);
        }

        if (result.Failure == PortalFailure.Invalid)
        {
            ModelState.AddModelError(result.Field!, result.Message!);
            return Common.Errors.ValidationProblems.Create(HttpContext, ModelState);
        }

        var (status, code, title, detail) = Describe(result);
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = HttpContext.Items[CorrelationIdMiddleware.ItemKey];
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    private static (int Status, string Code, string Title, string Detail) Describe<T>(PortalResult<T> result) =>
        result.Failure switch
        {
            PortalFailure.LinkNotFound => (404, "link_not_found", "This link is not valid",
                "We could not find this request. Check that you opened the whole link from your email."),
            PortalFailure.LinkRevoked => (410, "link_revoked", "This link is no longer active",
                "This link was withdrawn. Contact your lender for a new one."),
            PortalFailure.LinkExpired => (410, "link_expired", "This link has expired",
                "This link has expired. Contact your lender for a new one."),
            PortalFailure.PasswordRequired => (401, "password_required", "Password required",
                "Enter the password your lender gave you."),
            PortalFailure.InvalidPassword => (401, "invalid_password", "Incorrect password",
                "That password is not correct. Try again."),
            PortalFailure.NotFound => (404, "not_found", "Not found", "We could not find that."),
            PortalFailure.PrivacyRequired => (403, "privacy_required", "Privacy notice not accepted",
                "Accept the privacy notice before you add anything."),
            PortalFailure.ItemLocked => (409, "item_locked", "This item is approved",
                "This item has already been approved and can no longer be changed."),
            PortalFailure.LoanLocked => (409, "loan_completed", "This request is completed",
                "This loan is completed and locked. Ask your lender to reopen the loan before making changes."),
            PortalFailure.File => DescribeFile(result.FileError!.Value),
            _ => (400, "invalid", "The request is not valid", "The request is not valid."),
        };

    private static (int, string, string, string) DescribeFile(FileError error) => error switch
    {
        FileError.ItemNotFound or FileError.FileNotFound => (404, "not_found", "Not found", "We could not find that."),
        FileError.UnsupportedType => (400, "unsupported_type", "File type not allowed",
            "Upload a PDF, JPG, PNG, DOCX, XLSX or CSV file."),
        FileError.TooLarge => (400, "too_large", "File too large", "Files can be at most 25 MB."),
        FileError.Empty => (400, "empty_file", "File is empty", "That file is empty."),
        FileError.NotUploaded => (409, "not_uploaded", "Upload not finished", "The file did not finish uploading. Try again."),
        FileError.NotReady => (409, "not_ready", "File not ready", "That file cannot be completed."),
        FileError.NotAFileItem => (400, "not_a_file_item", "Not a file item", "This item does not take files."),
        _ => (400, "invalid_file", "File not accepted",
            "The file could not be accepted. Check that it is a valid file of the right type and try again."),
    };
}

public static class PortalServiceCollectionExtensions
{
    /// <summary>The portal: token exchange, the session scheme, rate limiting and the portal services.</summary>
    public static IServiceCollection AddPortal(this IServiceCollection services)
    {
        services.AddOptions<PortalSessionOptions>().BindConfiguration(PortalSessionOptions.Section);
        services.AddSingleton<PortalSessionKey>();
        services.AddSingleton<IPasswordHasher<Invite>, PasswordHasher<Invite>>();
        services.AddScoped<PortalSessionIssuer>();
        services.AddScoped<PortalService>();

        services.AddAuthentication()
            .AddJwtBearer(PortalAuth.Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(PortalAuth.Scheme)
            .Configure<PortalSessionKey>(PortalSessionBinding.Configure);

        services.AddAuthorizationBuilder()
            .AddPolicy(PortalAuth.Policy, policy => policy
                .AddAuthenticationSchemes(PortalAuth.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(PortalAuth.InviteClaim));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PortalAuth.RateLimitPolicy, httpContext =>
            {
                var limit = httpContext.RequestServices.GetRequiredService<IOptions<PortalSessionOptions>>().Value.RateLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, limit),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many attempts",
                    Detail = "Too many attempts. Wait a minute and try again.",
                    Extensions = { ["code"] = "rate_limited" },
                }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", token);
            };
        });
        return services;
    }

    public static WebApplication UsePortal(this WebApplication app)
    {
        // Portal responses carry the secret-bearing flow: the link token must never leak through a Referer
        // header, and nothing is cached.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/portal"))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
            }

            await next();
        });
        app.UseRateLimiter();
        return app;
    }
}

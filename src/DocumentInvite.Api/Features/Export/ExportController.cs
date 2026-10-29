using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DocumentInvite.Api.Features.Export;

/// <summary>Package export is for Admins and Requesters only.</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.RequireStaff)]
public class ExportController(ExportService service) : ControllerBase
{
    /// <summary>Streams a zip of the request's approved files and form answers straight to the response.</summary>
    [HttpGet("api/requests/{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        var plan = await service.PlanAsync(id, ct);
        if (plan is null)
        {
            return NotFound();
        }

        if (plan.IsEmpty)
        {
            return Problem(detail: "This request has no approved documents or answers to export yet.", statusCode: StatusCodes.Status409Conflict);
        }

        await service.AuditAsync(plan, ct);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = plan.ArchiveName,
        }.ToString();
        Response.Headers.CacheControl = "no-store";

        // ZipArchive writes synchronously, in small chunks, so allow that for this response only.
        var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        await service.WriteZipAsync(plan, Response.Body, ct);
        return new EmptyResult();
    }
}

public static class ExportServiceCollectionExtensions
{
    public static IServiceCollection AddExport(this IServiceCollection services) =>
        services.AddScoped<ExportService>();
}

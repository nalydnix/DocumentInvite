using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Gdpr;

/// <summary>GDPR requests for a single contact. Export is for staff; erasure is Admin only.</summary>
[ApiController]
[Route("api/gdpr/contacts/{id:guid}")]
public class GdprController(GdprService service) : ControllerBase
{
    /// <summary>Streams a zip with <c>contact.json</c> and the contact's files.</summary>
    [HttpGet("export")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        var result = await service.PrepareExportAsync(id, ct);
        return result.Value is { } package ? new ExportZipResult(package) : NotFound();
    }

    [HttpPost("erase")]
    [Authorize(Policy = AuthPolicies.RequireAdmin)]
    public async Task<IActionResult> Erase(Guid id, EraseContactRequest request, CancellationToken ct)
    {
        var result = await service.EraseAsync(id, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        return result.Conflict is not null
            ? Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict)
            : Ok(result.Value);
    }
}

/// <summary>The retention purge: a dry-run preview and the real run. Admin only.</summary>
[ApiController]
[Route("api/gdpr/retention")]
[Authorize(Policy = AuthPolicies.RequireAdmin)]
public class RetentionController(RetentionService service) : ControllerBase
{
    /// <summary>Lists the loans and file counts a run would purge. Changes nothing.</summary>
    [HttpGet("preview")]
    public async Task<RetentionPreviewResponse> Preview([FromQuery] PageQuery query, CancellationToken ct) =>
        await service.PreviewAsync(query, ct);

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var result = await service.RunAsync(ct);
        return result.Conflict is not null
            ? Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict)
            : Ok(result.Value);
    }
}

/// <summary>Writes the zip straight to the response body.</summary>
internal sealed class ExportZipResult(ContactExportPackage package) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = "application/zip";
        response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition
        {
            FileName = package.FileName,
            DispositionType = "attachment",
        }.ToString();
        response.Headers.CacheControl = "no-store";

        // ZipArchive flushes its deflate buffers synchronously when an entry closes. Allow that for this response only.
        var bodyControl = context.HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        await package.WriteToAsync(response.Body, context.HttpContext.RequestAborted);
    }
}

public static class GdprServiceCollectionExtensions
{
    public static IServiceCollection AddGdpr(this IServiceCollection services) =>
        services.AddScoped<GdprService>().AddScoped<RetentionService>();
}

using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Audit;

[ApiController]
[Route("api/audit")]
[Authorize(Policy = AuthPolicies.CanAudit)]
public class AuditController(AuditQueryService service) : ControllerBase
{
    [HttpGet]
    public async Task<AuditPage> List([FromQuery] AuditQuery query, CancellationToken ct) =>
        await service.ListAsync(query, ct);

    [HttpGet("export.csv")]
    public async Task ExportCsv([FromQuery] AuditQuery query, CancellationToken ct)
    {
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=\"audit-export.csv\"";
        Response.Headers.CacheControl = "no-store";
        await service.ExportCsvAsync(query, Response.Body, ct);
    }

    [HttpPost("verify")]
    public async Task<AuditVerifyResponse> Verify(CancellationToken ct) => await service.VerifyAsync(ct);
}

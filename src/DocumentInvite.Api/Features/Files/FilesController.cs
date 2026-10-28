using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Files;

/// <summary>Short-lived read links for staff. Every link issued is audited.</summary>
[ApiController]
public class FilesController(FileReadService service) : ControllerBase
{
    /// <summary>A viewer may preview a file inline.</summary>
    [HttpGet("api/files/{id:guid}/preview-url")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct) =>
        ToActionResult(await service.CreateReadUrlAsync(id, false, ct));

    /// <summary>Downloading is for staff only: a viewer cannot download.</summary>
    [HttpGet("api/files/{id:guid}/download-url")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct) =>
        ToActionResult(await service.CreateReadUrlAsync(id, true, ct));

    private IActionResult ToActionResult(FileResult<FileReadUrl> result) => result.Error switch
    {
        null => Ok(result.Value),
        FileError.FileNotFound => NotFound(),
        _ => Problem(detail: "The file is not available yet.", statusCode: StatusCodes.Status409Conflict),
    };
}

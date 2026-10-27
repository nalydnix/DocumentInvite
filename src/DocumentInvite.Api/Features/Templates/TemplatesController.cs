using DocumentInvite.Api.Common.Errors;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Templates;

[ApiController]
[Route("api/templates")]
public class TemplatesController(TemplateService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IReadOnlyList<TemplateSummaryResponse>> List(
        [FromQuery] bool includeArchived, CancellationToken ct) => await service.ListAsync(includeArchived, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => ToActionResult(await service.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Create(SaveTemplateRequest request, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = result.Template!.Id }, result.Template);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Update(Guid id, SaveTemplateRequest request, CancellationToken ct) =>
        ToActionResult(await service.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        ToActionResult(await service.ArchiveAsync(id, ct));

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) =>
        ToActionResult(await service.RestoreAsync(id, ct));

    private IActionResult ToActionResult(TemplateResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Conflict is not null)
        {
            ModelState.AddModelError("template", result.Conflict);
            return ValidationProblems.Create(HttpContext, ModelState);
        }

        return Ok(result.Template);
    }
}

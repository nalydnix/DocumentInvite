using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Requests;

[ApiController]
[Route("api/requests")]
public class RequestsController(RequestService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<PagedResponse<RequestSummaryResponse>> List([FromQuery] RequestListQuery query, CancellationToken ct) =>
        await service.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => ToActionResult(await service.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Create(SaveRequestRequest input, CancellationToken ct)
    {
        var result = await service.CreateAsync(input, ct);
        return result.Request is { } request ? CreatedAtAction(nameof(Get), new { id = request.Id }, request) : ToActionResult(result);
    }

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) => ToActionResult(await service.CompleteAsync(id, ct));

    [HttpPost("{id:guid}/reopen")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Reopen(Guid id, ReopenRequestRequest input, CancellationToken ct) =>
        ToActionResult(await service.ReopenAsync(id, input.Reason!, ct));

    private IActionResult ToActionResult(RequestResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Conflict is not null)
        {
            return Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        return Ok(result.Request);
    }
}

public static class RequestsServiceCollectionExtensions
{
    public static IServiceCollection AddRequests(this IServiceCollection services) =>
        services.AddScoped<RequestService>();
}

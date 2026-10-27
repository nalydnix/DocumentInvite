using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Contacts;

[ApiController]
[Route("api/contacts")]
public class ContactsController(ContactService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<PagedResponse<ContactResponse>> List([FromQuery] ContactListQuery query, CancellationToken ct) =>
        await service.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => ToActionResult(await service.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Create(ContactRequest request, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.Contact is { } contact
            ? CreatedAtAction(nameof(Get), new { id = contact.Id }, contact)
            : ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Update(Guid id, ContactRequest request, CancellationToken ct) =>
        ToActionResult(await service.UpdateAsync(id, request, ct));

    private IActionResult ToActionResult(ContactResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Conflict is not null)
        {
            return Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        return Ok(result.Contact);
    }
}

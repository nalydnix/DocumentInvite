using DocumentInvite.Api.Common.Errors;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Users;

[ApiController]
[Route("api/users")]
[Authorize(Policy = AuthPolicies.RequireAdmin)]
public class UsersController(UserAdminService service) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<UserResponse>> List(CancellationToken ct) => await service.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id) => ToActionResult(await service.GetAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.User is { } user
            ? CreatedAtAction(nameof(Get), new { id = user.Id }, user)
            : ToActionResult(result);
    }

    [HttpPut("{id:guid}/roles")]
    public async Task<IActionResult> SetRoles(Guid id, SetRolesRequest request, CancellationToken ct) =>
        ToActionResult(await service.SetRolesAsync(id, request, ct));

    [HttpPut("{id:guid}/access-expiry")]
    public async Task<IActionResult> SetAccessExpiry(Guid id, SetAccessExpiryRequest request, CancellationToken ct) =>
        ToActionResult(await service.SetAccessExpiryAsync(id, request, ct));

    private IActionResult ToActionResult(UserResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Errors is not null)
        {
            foreach (var (field, messages) in result.Errors)
            {
                foreach (var message in messages)
                {
                    ModelState.AddModelError(field, message);
                }
            }

            return ValidationProblems.Create(HttpContext, ModelState);
        }

        return Ok(result.User);
    }
}

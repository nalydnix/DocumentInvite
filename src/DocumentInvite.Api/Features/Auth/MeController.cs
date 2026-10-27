using System.Security.Claims;
using DocumentInvite.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Auth;

public sealed record MeResponse(
    Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, DateTimeOffset? AccessExpiresAt);

[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(UserManager<AppUser> users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MeResponse>> Get()
    {
        var user = await users.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty);
        if (user is null)
        {
            return Unauthorized();
        }

        var roles = (await users.GetRolesAsync(user)).Order().ToList();
        return new MeResponse(user.Id, user.Email ?? string.Empty, user.DisplayName, roles, user.AccessExpiresAt);
    }
}

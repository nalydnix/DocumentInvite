using DocumentInvite.Api.Common.Errors;
using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Invites;

[ApiController]
public class InvitesController(InviteService service) : ControllerBase
{
    [HttpGet("api/invites/{id:guid}")]
    [Authorize(Policy = AuthPolicies.CanRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => ToActionResult(await service.GetAsync(id, ct));

    /// <summary>Creates one invite per party and emails each party its link.</summary>
    [HttpPost("api/loans/{loanId:guid}/invites")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Send(Guid loanId, SendInvitesRequest request, CancellationToken ct)
    {
        var result = await service.SendAsync(loanId, request, ct);
        return result.Value is { } invites ? StatusCode(StatusCodes.Status201Created, invites) : ToActionResult(result);
    }

    [HttpPost("api/invites/{id:guid}/revoke")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) => ToActionResult(await service.RevokeAsync(id, ct));

    [HttpPost("api/invites/{id:guid}/reissue")]
    [Authorize(Policy = AuthPolicies.RequireStaff)]
    public async Task<IActionResult> Reissue(Guid id, CancellationToken ct) => ToActionResult(await service.ReissueAsync(id, ct));

    private IActionResult ToActionResult<T>(InviteResult<T> result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Conflict is not null)
        {
            return Problem(detail: result.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        if (result.InvalidField is not null)
        {
            ModelState.AddModelError(result.InvalidField, result.InvalidMessage!);
            return ValidationProblems.Create(HttpContext, ModelState);
        }

        return Ok(result.Value);
    }
}

public static class InvitesServiceCollectionExtensions
{
    public static IServiceCollection AddInvites(this IServiceCollection services)
    {
        services.AddScoped<InviteService>();
        services.AddScoped<InviteTokenService>();
        return services;
    }
}

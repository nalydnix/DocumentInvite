using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Settings;

[ApiController]
[Route("api/settings")]
[Authorize(Policy = AuthPolicies.RequireAdmin)]
public class SettingsController(SettingsService service) : ControllerBase
{
    [HttpGet]
    public async Task<SettingsResponse> Get(CancellationToken ct) => await service.GetAsync(ct);

    [HttpPut]
    public async Task<IActionResult> Update(UpdateSettingsRequest request, CancellationToken ct)
    {
        var result = await service.UpdateAsync(request, ct);
        return result is null
            ? Problem(detail: "The settings were changed by someone else. Reload and try again.", statusCode: StatusCodes.Status409Conflict)
            : Ok(result);
    }
}

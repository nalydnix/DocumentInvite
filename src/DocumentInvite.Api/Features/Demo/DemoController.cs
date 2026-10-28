using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Features.Demo;

[ApiController]
[Route("api/demo")]
public class DemoController(DemoService service) : ControllerBase
{
    /// <summary>Public: the sign-in page shows the Admin and Auditor logins when demo mode is on.</summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public DemoConfigResponse GetConfig() => service.GetConfig();

    [HttpPost("reset")]
    [Authorize(Policy = AuthPolicies.RequireAdmin)]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        var result = await service.ResetAsync(ct);
        return result is null ? NotFound() : Ok(result);
    }
}

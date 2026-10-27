using DocumentInvite.Api.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocumentInvite.Api.Tests.Infrastructure;

/// <summary>Test-only endpoints, one per authorisation policy, standing in for real feature endpoints.</summary>
[ApiController]
[Route("test/policy")]
public class PolicyProbeController : ControllerBase
{
    [HttpGet("admin"), Authorize(Policy = AuthPolicies.RequireAdmin)]
    public IActionResult Admin() => Ok();

    [HttpGet("staff"), Authorize(Policy = AuthPolicies.RequireStaff)]
    public IActionResult Staff() => Ok();

    [HttpGet("read"), Authorize(Policy = AuthPolicies.CanRead)]
    public IActionResult Read() => Ok();

    [HttpGet("audit"), Authorize(Policy = AuthPolicies.CanAudit)]
    public IActionResult Audit() => Ok();

    [HttpGet("any"), Authorize]
    public IActionResult Any() => Ok();
}
